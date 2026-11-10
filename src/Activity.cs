using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace LumaShell;

internal sealed class Activity
{
    internal sealed record Item(uint Id, DateTimeOffset Time, string App, string Title, string Detail);
    private UserNotificationListener? listener;
    private Item[] latest = [];
    private HashSet<(uint, DateTimeOffset)>? seen;
    private int revision;
    private bool updating;
    internal bool Enabled => listener is not null;
    internal event Action<Item[], string>? Updated;
    internal event Action<string>? Arrived;

    internal async void Toggle()
    {
        if (updating && !Enabled) return;
        if (Enabled)
        {
            Disable("Доступ выключен. Записи удалены из Luma");
            return;
        }
        updating = true;
        try
        {
            var current = UserNotificationListener.Current;
            var access = await current.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Disable("Windows не выдала доступ к уведомлениям");
                return;
            }
            listener = current;
            Updated?.Invoke([], "Подключаем центр уведомлений…");
        }
        catch (Exception error) when (Unavailable(error)) { Disable("Доступ к уведомлениям недоступен в этой конфигурации Windows"); }
        finally { updating = false; }
        Refresh();
    }

    internal async void Refresh()
    {
        if (listener is not { } current || updating) return;
        int generation = revision;
        updating = true;
        try
        {
            if (current.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Disable("Windows отозвала доступ к уведомлениям");
                return;
            }
            var notifications = await current.GetNotificationsAsync(NotificationKinds.Toast);
            if (generation != revision) return;
            var active = notifications.Select(n => (n.Id, n.CreationTime)).ToHashSet();
            var items = notifications.OrderByDescending(n => n.CreationTime).Take(15).Select(Read).ToArray();
            bool initial = seen is null;
            var fresh = seen is null ? null : items.FirstOrDefault(n => !seen.Contains((n.Id, n.Time)) &&
                DateTimeOffset.UtcNow - n.Time < TimeSpan.FromSeconds(30));
            seen = active;
            if (initial || !latest.SequenceEqual(items))
            {
                latest = items;
                Updated?.Invoke(items, items.Length == 0 ? "В центре уведомлений пока пусто" : "Из центра уведомлений Windows");
            }
            if (fresh is not null) Arrived?.Invoke(fresh.App);
        }
        catch (Exception error) when (Unavailable(error))
        {
            if (generation == revision) Disable("Windows не вернула список уведомлений");
        }
        finally { updating = false; }
    }

    private void Disable(string message)
    {
        revision++;
        listener = null;
        seen = null;
        latest = [];
        Updated?.Invoke([], message);
    }

    private static Item Read(UserNotification notice)
    {
        string app = "Windows";
        try { app = notice.AppInfo.DisplayInfo.DisplayName; }
        catch (Exception error) when (Unavailable(error)) { }
        var text = notice.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
        return new Item(notice.Id, notice.CreationTime, Visuals.Label(app, 65),
            Visuals.Label(text?.FirstOrDefault()?.Text ?? "Уведомление", 140),
            Visuals.Label(string.Join(" · ", text?.Skip(1).Select(item => item.Text) ?? []), 240));
    }

    private static bool Unavailable(Exception error) => error is COMException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or ArgumentException;
}
