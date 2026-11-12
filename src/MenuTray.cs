using System;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class MenuTray
{
    private readonly StackPanel bar = new() { Orientation = Orientation.Horizontal };
    private readonly Popup popup = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
    private readonly TextBlock date = Text("", 12, bold: true);
    private readonly TextBlock inputLanguage = Text("", 11, bold: true);
    private readonly TextBlock battery = Text("", 12);
    private readonly TextBlock details = Text("", 12, Muted);
    private readonly TextBlock sound = Text("Звук", 12, bold: true);
    private readonly TextBlock mute = Text("Выключить звук", 12, bold: true);
    private readonly TextBlock updates = Text("Проверить обновления", 11, Blue, true);
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Width = 164, Margin = new Thickness(0, 3, 0, 0) };
    private readonly FrameworkElement networkIcon;
    private bool settingVolume;
    private string? displayedLanguage;

    internal FrameworkElement View => bar;

    internal MenuTray(Action showNotifications, Action appTray, Action update, Updates release)
    {
        inputLanguage.Margin = new Thickness(4, 1, 4, 1);
        inputLanguage.MinWidth = 20;
        inputLanguage.TextAlignment = TextAlignment.Center;
        inputLanguage.Visibility = Visibility.Collapsed;
        var languageBadge = Surface(Brushes.Transparent, 5, inputLanguage);
        languageBadge.BorderBrush = Paint("#62FFFFFF");
        languageBadge.BorderThickness = new Thickness(1);
        languageBadge.Margin = new Thickness(6, 0, 3, 0);
        languageBadge.VerticalAlignment = VerticalAlignment.Center;
        languageBadge.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = inputLanguage });
        bar.Children.Add(languageBadge);
        networkIcon = Icon("M2,6 Q10,-1 18,6 M5,9 Q10,5 15,9 M8,12 Q10,10 12,12 M10,15 L10.1,15", 16);
        bar.Children.Add(Button(networkIcon, "Сеть", () => Desktop.Launch("ms-settings:network"), Brushes.Transparent, 6));
        var power = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 1, 0) };
        var batteryIcon = Icon("M2,5 H16 V14 H2 Z M18,8 V11 M5,8 H13 M5,11 H13", 17);
        batteryIcon.Margin = new Thickness(0, 0, 4, 0);
        power.Children.Add(batteryIcon);
        power.Children.Add(battery);
        power.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = battery });
        bar.Children.Add(power);
        var controls = Button(Icon("M3,6 L17,6 M3,13 L17,13 M7,3 L7,9 M13,10 L13,16", 16),
            "Пункт управления Luma", () => { popup.IsOpen = !popup.IsOpen; Refresh(); }, Brushes.Transparent, 7);
        bar.Children.Add(controls);
        bar.Children.Add(Button(date, "Открыть события", showNotifications, Brushes.Transparent, 7));
        popup.PlacementTarget = controls;
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (size, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - size.Width, target.Height + 6), PopupPrimaryAxis.Horizontal)];

        var menu = new StackPanel { Width = 288 };
        var title = Text("Пункт управления", 17, bold: true);
        title.Margin = new Thickness(4, 2, 0, 1);
        details.Margin = new Thickness(4, 0, 0, 9);
        menu.Children.Add(title);
        menu.Children.Add(details);

        var switches = new UniformGrid { Columns = 2, Rows = 1, Margin = new Thickness(-3, 0, -3, 8) };
        switches.Children.Add(ControlTile("Сеть", "M2,6 Q10,-1 18,6 M5,9 Q10,5 15,9 M8,12 Q10,10 12,12 M10,15 L10.1,15",
            () => Desktop.Launch("ms-settings:network")));
        switches.Children.Add(ControlTile("Уведомления", "M4,15 H16 M6,14 V8 A4,4 0 0 1 14,8 V14 M8,17 Q10,20 12,17", showNotifications));
        menu.Children.Add(switches);

        var audio = new StackPanel();
        audio.Children.Add(sound);
        audio.Children.Add(volume);
        var row = new DockPanel();
        var muteButton = Button(mute, "Переключить беззвучный режим", () => { Audio.ToggleMute(); RefreshAudio(); }, Chip, 6);
        DockPanel.SetDock(muteButton, Dock.Right);
        muteButton.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(muteButton);
        volume.Width = 157;
        volume.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(audio);
        var card = Surface(Paint("#45202025"), 14, row);
        card.Padding = new Thickness(11);
        menu.Children.Add(card);
        volume.ValueChanged += (_, _) => { if (!settingVolume) { Audio.SetLevel(volume.Value); sound.Text = $"Звук · {volume.Value:P0}"; } };

        menu.Children.Add(TrayButton("Питание и батарея", () => Desktop.Launch("ms-settings:powersleep")));
        menu.Children.Add(TrayButton("Трей приложений Luma", appTray));
        var updateButton = Button(updates, "Обновления Luma", () => { popup.IsOpen = false; update(); }, Card);
        updateButton.Margin = new Thickness(0, 7, 0, 0);
        updateButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        menu.Children.Add(updateButton);
        release.Changed += () => updates.Text = release.Ready ? "Установить обновление" : release.Status;
        var container = Surface(Gradient("#EF36383F", "#FA23252A"), 21, menu);
        container.Padding = new Thickness(12);
        container.BorderBrush = Paint("#66FFFFFF");
        container.BorderThickness = new Thickness(1);
        popup.Child = container;
        popup.Opened += (_, _) => Entrance(container);
        controls.Unloaded += (_, _) => popup.IsOpen = false;
    }

    private Button ControlTile(string label, string glyph, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icon(glyph, 15);
        var badge = Surface(Paint("#FF2D63B1"), 14, icon);
        badge.Width = badge.Height = 29;
        badge.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(badge);
        row.Children.Add(Text(label, 12, bold: true));
        var button = Button(row, label, () => { popup.IsOpen = false; action(); }, Paint("#56202025"), 9);
        button.Margin = new Thickness(3, 0, 3, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }

    private Button TrayButton(string name, Action action)
    {
        var button = Button(name, () => { popup.IsOpen = false; action(); }, Card);
        button.Margin = new Thickness(0, 4, 0, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }

    internal void Refresh()
    {
        string? charge = Native.BatteryText();
        battery.Text = charge ?? "";
        battery.Visibility = charge is null ? Visibility.Collapsed : Visibility.Visible;
        date.Text = DateTime.Now.ToString("ddd, d MMM   HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        bool connected = NetworkInterface.GetIsNetworkAvailable();
        networkIcon.Opacity = connected ? 1 : 0.48;
        details.Text = (connected ? "Сеть доступна" : "Нет сети") + (charge is null ? "" : $"   ·   {charge}");
        if (popup.IsOpen) RefreshAudio();
    }

    internal void RefreshInputLanguage(CultureInfo? culture)
    {
        if (culture is null || culture.Name == displayedLanguage) return;
        displayedLanguage = culture.Name;
        inputLanguage.Text = culture.TwoLetterISOLanguageName.ToUpperInvariant();
        string label = $"Язык ввода: {culture.DisplayName}";
        inputLanguage.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(inputLanguage, label);
        inputLanguage.Visibility = Visibility.Visible;
    }

    private void RefreshAudio()
    {
        var audio = Audio.Status();
        volume.IsEnabled = audio is not null;
        if (audio is not { } state) { sound.Text = "Нет выхода"; return; }
        settingVolume = true;
        volume.Value = state.Level;
        settingVolume = false;
        sound.Text = $"Звук · {state.Level:P0}";
        mute.Text = state.Muted ? "Включить звук" : "Выключить звук";
    }
}
