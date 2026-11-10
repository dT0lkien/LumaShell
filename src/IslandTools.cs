using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed partial class Island
{
    private readonly Screenshots screenshots;
    private readonly Activity activity;
    private readonly Prism prism;
    private readonly Preferences preferences;
    private readonly Action toggleShortcut;
    private readonly StackPanel shotItems = new();
    private readonly StackPanel noticeItems = new();
    private readonly TextBlock shotFolder = Text("", 11, Muted);
    private readonly TextBlock shotShortcut = Text("", 11, Blue);
    private readonly TextBlock noticeStatus = Text("Подключите центр уведомлений Windows", 12, Muted);
    private readonly TextBlock noticeButtonLabel = Text("Включить", 12, bold: true);
    private readonly TextBlock overviewNotices = Text("Центр уведомлений", 13, bold: true);
    private readonly TextBlock prismStatus = Text("Статус выключен", 12, bold: true);
    private readonly TextBlock prismToggleLabel = Text("Показать статус", 12, bold: true);
    private readonly TextBlock zapretStatus = Text("Zapret · ищем службу Prism", 11, Muted, true);
    private readonly TextBlock zapretAction = Text("Настроить в Prism", 11, bold: true);

    private FrameworkElement BuildHome()
    {
        var layout = new DockPanel();
        var row = new DockPanel { Margin = new Thickness(2, 7, 2, 0) };
        var prismActions = Row(Button("Открыть", () => Prism.Open()),
            Button(prismToggleLabel, "Включить или выключить чтение статуса Prism", prism.Toggle),
            Button(Text("⚙", 13, bold: true), "Указать папку профиля Prism", prism.ChooseProfile, Chip, 6));
        DockPanel.SetDock(prismActions, Dock.Right);
        row.Children.Add(prismActions);
        var vpn = Stack(Text("PRISM VPN", 10, Blue, true), prismStatus);
        vpn.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(vpn);
        var zapret = new DockPanel { Margin = new Thickness(2, 6, 2, 0) };
        var action = Button(zapretAction, "Переключить службу zapret или открыть Prism", () =>
        {
            if (prism.ChangingZapret || prism.ZapretStatus == Zapret.Mode.Pending) return;
            if (prism.ZapretStatus is Zapret.Mode.On or Zapret.Mode.Off) prism.ToggleZapret();
            else Prism.Open();
        }, Chip, 7);
        DockPanel.SetDock(action, Dock.Right);
        zapret.Children.Add(action);
        zapret.Children.Add(zapretStatus);
        var prismCard = Surface(Card, 14, Stack(row, zapret));
        prismCard.Padding = new Thickness(10, 2, 7, 9);
        DockPanel.SetDock(prismCard, Dock.Bottom);
        layout.Children.Add(prismCard);
        var tiles = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(0, 0, 0, 7) };
        tiles.Children.Add(QuickCard("⌘   СНИМКИ", Text("Выбрать область", 13, bold: true), screenshots.Begin));
        tiles.Children.Add(QuickCard("●   СОБЫТИЯ", overviewNotices, () => Select(IslandTab.Notifications)));
        tiles.Children.Add(QuickCard("♫   МУЗЫКА", overviewMusic, () => Select(IslandTab.Music)));
        tiles.Children.Add(QuickCard("◷   ФОКУС", overviewFocus, () => Select(IslandTab.Focus)));
        layout.Children.Add(tiles);
        return layout;
    }

    private static Button QuickCard(string label, TextBlock value, Action action)
    {
        var card = Button(Stack(Text(label, 10, Blue, true), value), label, action, Card, 13);
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Margin = new Thickness(3);
        return card;
    }

    private FrameworkElement BuildScreenshots()
    {
        var head = new DockPanel();
        var buttons = Row(Button("Сделать снимок", screenshots.Begin), Button("Папка", screenshots.OpenFolder),
            Button("Куда сохранять…", () => { if (screenshots.ChooseFolder()) RefreshScreenshots(); }));
        buttons.HorizontalAlignment = HorizontalAlignment.Left;
        var shortcut = Button(shotShortcut, "Переключить Win+Shift+S между Luma и Ножницами Windows", toggleShortcut, Card);
        shortcut.HorizontalContentAlignment = HorizontalAlignment.Left;
        shortcut.Margin = new Thickness(2, 6, 2, 0);
        shotFolder.Margin = new Thickness(5, 7, 5, 6);
        var section = Stack(buttons, shortcut, shotFolder);
        DockPanel.SetDock(section, Dock.Top);
        head.Children.Add(section);
        head.Children.Add(new ScrollViewer { Content = shotItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return head;
    }

    internal void RefreshScreenshots()
    {
        shotShortcut.Text = preferences.ReplaceSnip ? "●  Win+Shift+S — Luma, сохранение в PNG" : "○  Включить Win+Shift+S для Luma";
        shotFolder.Text = screenshots.Folder;
        shotFolder.ToolTip = screenshots.Folder;
        shotItems.Children.Clear();
        string[] paths = screenshots.Recent();
        if (paths.Length == 0)
        {
            var empty = Text("Снимки появятся здесь после сохранения", 12, Muted);
            empty.Margin = new Thickness(7, 9, 0, 0);
            shotItems.Children.Add(empty);
            return;
        }
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string label = name.Length >= 24 ? name[5..24].Replace('_', ' ') : name;
            var row = Button(Text($"▧   Снимок {label}", 12), "Открыть снимок", () =>
            {
                if (!File.Exists(path) || !Desktop.Launch(path)) Flash("Снимок перемещён или недоступен");
            }, Card);
            row.HorizontalContentAlignment = HorizontalAlignment.Left;
            row.Margin = new Thickness(2, 0, 2, 4);
            shotItems.Children.Add(row);
        }
    }

    private FrameworkElement BuildActivity()
    {
        var layout = new DockPanel();
        var heading = new DockPanel { Margin = new Thickness(4, 0, 0, 8) };
        var toggle = Button(noticeButtonLabel, "Включить или выключить доступ к уведомлениям", activity.Toggle);
        DockPanel.SetDock(toggle, Dock.Right);
        heading.Children.Add(toggle);
        heading.Children.Add(noticeStatus);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        layout.Children.Add(new ScrollViewer { Content = noticeItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return layout;
    }

    private void RenderActivity(Activity.Item[] items, string status)
    {
        noticeStatus.Text = status;
        noticeStatus.ToolTip = status;
        noticeButtonLabel.Text = activity.Enabled ? "Выключить" : "Включить";
        overviewNotices.Text = items.Length == 0 ? "Центр уведомлений" : $"В центре Windows · {items.Length}";
        noticeItems.Children.Clear();
        foreach (var item in items)
        {
            var headline = Text(item.Title, 13, bold: true);
            var app = Text($"{item.App}   ·   {item.Time.ToLocalTime():HH:mm}", 11, Blue);
            var contents = Stack(app, headline);
            if (item.Detail.Length > 0) contents.Children.Add(Text(item.Detail, 12, Muted));
            var card = Surface(Card, 12, contents);
            card.Padding = new Thickness(10, 7, 10, 7);
            card.Margin = new Thickness(2, 0, 2, 5);
            noticeItems.Children.Add(card);
        }
    }

    private void RefreshPrism()
    {
        prismStatus.Text = prism.Status;
        prismStatus.ToolTip = prism.Status;
        prismToggleLabel.Text = preferences.Prism ? "Скрыть статус" : "Показать статус";
        zapretStatus.Text = prism.ZapretStatus switch
        {
            Zapret.Mode.On => "●  Zapret · служба работает",
            Zapret.Mode.Off => "○  Zapret · служба остановлена",
            Zapret.Mode.Pending => "◌  Zapret · меняется состояние службы",
            Zapret.Mode.AppOwned => "●  Zapret работает в приложении Prism",
            _ => "Zapret · нужна служба Prism"
        };
        zapretAction.Text = prism.ChangingZapret ? "Переключаем…" : prism.ZapretStatus switch
        {
            Zapret.Mode.On => "Выключить",
            Zapret.Mode.Off => "Включить",
            Zapret.Mode.Pending => "Подождите…",
            _ => "Настроить в Prism"
        };
    }
}
