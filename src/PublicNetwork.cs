using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace LumaShell;

internal sealed class PublicNetwork : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.Zero
    }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 8192 };
    private bool refreshing;
    private bool disposed;
    private DateTimeOffset lastRequest;
    internal Address? Current { get; private set; }
    internal bool Refreshing => refreshing;
    internal event Action? Changed;

    internal async void Refresh(bool immediate = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (disposed || refreshing || (!immediate && now - lastRequest < TimeSpan.FromSeconds(12))) return;
        lastRequest = now;
        refreshing = true;
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.cloudflare.com/cdn-cgi/trace");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(request);
            Current = response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { Current = null; }
        finally
        {
            refreshing = false;
            if (!disposed) Changed?.Invoke();
        }
    }

    internal static Address? Parse(string trace)
    {
        string? ip = null, country = null;
        foreach (string line in trace.Split('\n'))
        {
            if (line.StartsWith("ip=", StringComparison.Ordinal) && IPAddress.TryParse(line.AsSpan(3).Trim(), out var address))
                ip = address.ToString();
            if (line.StartsWith("loc=", StringComparison.Ordinal))
            {
                var code = line.AsSpan(4).Trim();
                if (code.Length == 2 && code[0] is >= 'A' and <= 'Z' && code[1] is >= 'A' and <= 'Z' && !code.SequenceEqual("XX"))
                    country = code.ToString();
            }
        }
        return ip is null ? null : new Address(ip, country);
    }

    internal sealed record Address(string IP, string? Country);

    public void Dispose()
    {
        disposed = true;
        client.Dispose();
    }
}
