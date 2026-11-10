using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LumaShell;

internal sealed class Updates : IDisposable
{
    internal const string Repository = "dT0lkien/LumaShell";
    private static readonly string Cache = Path.Combine(Worker.Data, "updates");
    private static readonly string ResultPath = Path.Combine(Cache, "last-result.txt");
    private static readonly Version Current = typeof(Updates).Assembly.GetName().Version!;
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 1024 * 1024 };
    private Candidate? ready;
    private bool checking;
    internal bool Ready => ready is not null;
    internal string Status { get; private set; } = "Проверить обновления";
    internal event Action? Changed;

    internal Updates()
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LumaShell/{Current.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async void Check(bool interactive = false)
    {
        if (checking || Ready) return;
        if (Worker.Executable is null)
        {
            if (interactive) Report("Автообновление доступно в релизной сборке");
            return;
        }
        checking = true;
        if (interactive) Report("Проверяем GitHub…");
        try
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var asset = GetCandidate(json.RootElement, Current, AssetName());
            if (asset is null)
            {
                if (interactive) Report($"Установлена актуальная версия {Current.ToString(3)}");
                return;
            }
            Directory.CreateDirectory(Cache);
            string path = Path.Combine(Cache, $"{asset.Tag}-{AssetName()}");
            if (!Verify(path, asset.Digest))
            {
                Report($"Скачиваем {asset.Tag}…");
                await Download(asset, path);
            }
            ready = asset with { LocalPath = path };
            Report($"{asset.Tag} загружена · установить при выходе");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or
            IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { if (interactive || Status.StartsWith("Скачиваем", StringComparison.Ordinal)) Report("Не удалось проверить или скачать обновление"); }
        finally { checking = false; }
    }

    internal bool Schedule(bool restart)
    {
        if (ready is not { LocalPath: { } file } asset || Worker.Executable is not { } target) return false;
        if (Worker.Start("--apply-update", target, file, asset.Digest, restart ? "restart" : "exit", asset.Tag)) return true;
        Report("Не удалось запустить установку обновления");
        return false;
    }

    internal static string? ReadResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            string message = File.ReadAllText(ResultPath);
            File.Delete(ResultPath);
            return message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Report(string status) { Status = status; Changed?.Invoke(); }

    private async Task Download(Candidate asset, string path)
    {
        string temporary = path + ".part";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new IOException("Asset length mismatch");
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += count;
                    if (total > asset.Size) throw new IOException("Asset larger than expected");
                    await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                if (total != asset.Size) throw new IOException("Incomplete asset");
            }
            if (!Verify(temporary, asset.Digest)) throw new IOException("SHA-256 mismatch");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static Candidate? GetCandidate(JsonElement release, Version current, string name)
    {
        if (!release.TryGetProperty("tag_name", out var tagValue) || tagValue.GetString() is not { } tag ||
            !tag.StartsWith('v') || !Version.TryParse(tag.AsSpan(1), out var version) ||
            version.Revision >= 0 || version.Build < 0 || tag != $"v{version.ToString(3)}" ||
            version.CompareTo(new Version(current.Major, current.Minor, current.Build)) <= 0 ||
            !release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || !asset.TryGetProperty("digest", out var hash) ||
                hash.GetString() is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit)) continue;
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (asset.GetProperty("browser_download_url").GetString() != expected ||
                !asset.GetProperty("size").TryGetInt64(out long size) || size <= 0 || size > 160 * 1024 * 1024) continue;
            return new Candidate(tag, expected, digest[7..], size);
        }
        throw new IOException("Release does not contain a verified asset for this architecture");
    }

    private static string AssetName() => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "LumaShell-win-arm64.exe" : "LumaShell-win-x64.exe";

    private static bool Verify(string path, string digest)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Apply(Process? parent, string target, string downloaded, string digest, bool restart, string tag)
    {
        string? temporary = null;
        try
        {
            if (parent is not null && !parent.WaitForExit(30000)) throw new IOException("Previous process still running");
            if (!Path.IsPathFullyQualified(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(Path.GetFullPath(downloaded))?.Equals(Cache, StringComparison.OrdinalIgnoreCase) != true ||
                digest.Length != 64 || !Verify(downloaded, digest)) throw new IOException("Invalid update file");
            temporary = target + $".{Guid.NewGuid():N}.new";
            File.Copy(downloaded, temporary);
            if (!Verify(temporary, digest)) throw new IOException("Update copy failed verification");
            string backup = target + ".previous";
            File.Replace(temporary, target, backup, true);
            WriteResult($"Luma обновлена до {tag}");
            if (restart) Desktop.Launch(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            WriteResult("Обновление не установилось. Проверьте право записи в папку Luma");
            if (restart && File.Exists(target)) Desktop.Launch(target);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void WriteResult(string value)
    {
        try { Directory.CreateDirectory(Cache); File.WriteAllText(ResultPath, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record Candidate(string Tag, string Url, string Digest, long Size, string? LocalPath = null);
    public void Dispose() => client.Dispose();
}
