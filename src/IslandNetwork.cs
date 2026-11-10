using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed partial class Island
{
    private readonly PublicNetwork network;
    private readonly TextBlock ipText = Text("Определяем…", 13, bold: true);
    private readonly TextBlock countryText = Text("ВНЕШНИЙ IP", 10, Blue, true);
    private readonly TextBlock fallbackCode = Text("", 10, Blue, true);
    private readonly Image compactFlag = new() { Width = 19, Height = 14, Stretch = Stretch.Uniform };
    private readonly Image detailsFlag = new() { Width = 27, Height = 20, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly Border countryBadge = Surface(Chip, 5);
    private Button copyIP = null!;
    private Button refreshIP = null!;
    private string? shownCountry;

    private FrameworkElement BuildNetworkCard()
    {
        copyIP = Button("Копировать", () =>
        {
            if (network.Current is not { } value) return;
            try { Clipboard.SetText(value.IP); Flash("✓  IP скопирован"); }
            catch (ExternalException) { Flash("Буфер временно недоступен"); }
        });
        refreshIP = Button(Text("↻", 15, Blue, true), "Обновить внешний IP", () => network.Refresh(true), Chip, 7);
        var actions = Row(copyIP, refreshIP);
        var row = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        detailsFlag.Margin = new Thickness(1, 0, 9, 0);
        DockPanel.SetDock(detailsFlag, Dock.Left);
        row.Children.Add(detailsFlag);
        row.Children.Add(Stack(countryText, ipText));
        var panel = Surface(Card, 13, row);
        panel.Padding = new Thickness(9, 5, 5, 5);
        panel.Margin = new Thickness(0, 0, 0, 5);
        return panel;
    }

    private void RenderNetwork()
    {
        var address = network.Current;
        string? country = address?.Country;
        copyIP.IsEnabled = address is not null;
        refreshIP.IsEnabled = !network.Refreshing;
        ipText.Text = address?.IP ?? (network.Refreshing ? "Определяем…" : "IP недоступен");
        string? name = country;
        if (country is not null)
        {
            try { name = new RegionInfo(country).DisplayName; }
            catch (ArgumentException) { }
        }
        countryText.Text = name is null ? "ВНЕШНИЙ IP" : $"ВНЕШНИЙ IP  ·  {name}";
        countryBadge.ToolTip = address is null ? null : $"{name ?? "Внешний IP"} · {address.IP}";
        if (country == shownCountry) return;
        shownCountry = country;
        var image = country is null ? null : CountryFlags.Get(country);
        compactFlag.Source = detailsFlag.Source = image;
        detailsFlag.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        fallbackCode.Text = country ?? "";
        countryBadge.Child = image is null ? fallbackCode : compactFlag;
        countryBadge.Visibility = country is null ? Visibility.Collapsed : Visibility.Visible;
        if (country is not null && animate) Entrance(countryBadge, 3, 0.86);
    }
}
