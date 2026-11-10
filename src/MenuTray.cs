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
    private readonly TextBlock battery = Text("", 12);
    private readonly TextBlock details = Text("", 12, Muted);
    private readonly TextBlock sound = Text("Звук", 12, bold: true);
    private readonly TextBlock mute = Text("Выключить звук", 12, bold: true);
    private readonly TextBlock updates = Text("Проверить обновления", 11, Blue, true);
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Width = 164, Margin = new Thickness(0, 3, 0, 0) };
    private readonly FrameworkElement networkIcon;
    private bool settingVolume;

    internal FrameworkElement View => bar;

    internal MenuTray(Action showNotifications, Action nativeTray, Action update, Updates release)
    {
        networkIcon = Icon("M2,6 Q10,-1 18,6 M5,9 Q10,5 15,9 M8,12 Q10,10 12,12 M10,15 L10.1,15", 16);
        bar.Children.Add(Button(networkIcon, "Сеть", () => Desktop.Launch("ms-settings:network"), Brushes.Transparent, 6));
        battery.Margin = new Thickness(6, 0, 4, 0);
        bar.Children.Add(battery);
        var controls = Button(Icon("M3,6 L17,6 M3,13 L17,13 M7,3 L7,9 M13,10 L13,16", 16),
            "Пункт управления Luma", () => { popup.IsOpen = !popup.IsOpen; Refresh(); }, Brushes.Transparent, 7);
        bar.Children.Add(controls);
        bar.Children.Add(Button(date, "Открыть события", showNotifications, Brushes.Transparent, 7));
        popup.PlacementTarget = controls;
        popup.HorizontalOffset = -151;
        popup.VerticalOffset = 6;
        popup.PopupAnimation = SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None;

        var menu = new StackPanel { Width = 252 };
        var title = Text("Пункт управления", 17, bold: true);
        title.Margin = new Thickness(4, 2, 0, 1);
        details.Margin = new Thickness(4, 0, 0, 9);
        menu.Children.Add(title);
        menu.Children.Add(details);

        var audio = new StackPanel();
        audio.Children.Add(sound);
        audio.Children.Add(volume);
        var row = new DockPanel();
        var muteButton = Button(mute, "Переключить беззвучный режим", () => { Audio.ToggleMute(); RefreshAudio(); }, Chip, 6);
        DockPanel.SetDock(muteButton, Dock.Right);
        muteButton.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(muteButton);
        volume.Width = 105;
        volume.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(audio);
        var card = Surface(Card, 12, row);
        card.Padding = new Thickness(9);
        menu.Children.Add(card);
        volume.ValueChanged += (_, _) => { if (!settingVolume) { Audio.SetLevel(volume.Value); sound.Text = $"Звук · {volume.Value:P0}"; } };

        menu.Children.Add(TrayButton("Сеть и Интернет", () => Desktop.Launch("ms-settings:network")));
        menu.Children.Add(TrayButton("Уведомления Luma", showNotifications));
        menu.Children.Add(TrayButton("Питание и батарея", () => Desktop.Launch("ms-settings:powersleep")));
        menu.Children.Add(TrayButton("Значки приложений Windows", nativeTray));
        var updateButton = Button(updates, "Обновления Luma", () => { popup.IsOpen = false; update(); }, Card);
        updateButton.Margin = new Thickness(0, 7, 0, 0);
        updateButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        menu.Children.Add(updateButton);
        release.Changed += () => updates.Text = release.Ready ? "Установить обновление" : release.Status;
        var container = Surface(Gradient("#F22C2D37", "#FA14151D"), 20, menu);
        container.Padding = new Thickness(12);
        container.BorderBrush = Paint("#66FFFFFF");
        container.BorderThickness = new Thickness(1);
        popup.Child = container;
        controls.Unloaded += (_, _) => popup.IsOpen = false;
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
