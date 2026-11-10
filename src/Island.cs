using System;
using System.Collections.Generic;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static LumaShell.Visuals;

namespace LumaShell;

internal enum IslandTab { Home, Music, Screenshots, Notifications, Clipboard, Focus }

internal sealed partial class Island
{
    private const double CompactHeight = 38;
    private const double PanelWidth = 500;
    private const double PanelHeight = 348;
    private readonly Border shell;
    private readonly Grid panel;
    private readonly ContentControl body = new();
    private readonly TextBlock compactTime = Text("00:00", 12, bold: true);
    private readonly TextBlock compactActivity = Text("", 12, Blue, true);
    private readonly Grid activityGlyph = new() { Width = 17, Margin = new Thickness(0, 0, 9, 0) };
    private readonly StackPanel playbackBars = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock trackTitle = Text("Нет активного плеера", 18, bold: true);
    private readonly TextBlock trackArtist = Text("Включите музыку в приложении или браузере", 12, Muted);
    private readonly TextBlock overviewMusic = Text("Нет активного плеера", 13, bold: true);
    private readonly TextBlock overviewFocus = Text("Запустить таймер", 13, bold: true);
    private readonly TextBlock focusTime = Text("00:00", 38, bold: true);
    private readonly TextBlock focusStatus = Text("Выберите длительность", 12, Muted);
    private readonly TextBlock mediaHint = Text("Системный медиаплеер Windows", 11, Muted);
    private readonly ProgressBar mediaProgress = new() { Height = 4, Minimum = 0, Maximum = 1, Background = Chip, Foreground = Blue,
        Margin = new Thickness(18, 6, 18, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer hover;
    private readonly FocusTimer focus = new();
    private readonly Media media = new();
    private readonly List<string> history = new();
    private readonly Button playButton;
    private readonly Button previousButton;
    private readonly Button nextButton;
    private readonly Button pauseButton;
    private readonly Button resetButton;
    private readonly StackPanel clipItems = new();
    private readonly TextBlock clipHint = Text("История выключена", 12, Muted);
    private readonly Button clipToggle;
    private readonly Button[] tabs;
    private readonly FrameworkElement[] views;
    private readonly bool animate = SystemParameters.ClientAreaAnimation;
    private bool expanded;
    private bool pinned;
    private bool clipboardEnabled;
    private bool playingAnimation;
    private double targetWidth = 190;
    private IslandTab selected;
    private string? eventLabel;
    private DateTimeOffset eventUntil;
    private string? battery;

    internal Window Window { get; } = Overlay("Luma • Dynamic Island", PanelWidth, PanelHeight);
    internal bool Expanded => expanded;
    internal bool ClipboardEnabled => clipboardEnabled;
    internal event Action? ClipboardToggleRequested;
    internal event Action? Collapsed;

    internal Island(Screenshots screenshots, Activity activity, Prism prism, PublicNetwork network, Preferences preferences, Action toggleShortcut)
    {
        this.screenshots = screenshots;
        this.activity = activity;
        this.prism = prism;
        this.network = network;
        this.preferences = preferences;
        this.toggleShortcut = toggleShortcut;
        shell = Surface(Gradient("#FF0B0B10", "#FF10101B"), 17);
        shell.Width = targetWidth;
        shell.Height = CompactHeight;
        shell.HorizontalAlignment = HorizontalAlignment.Center;
        shell.VerticalAlignment = VerticalAlignment.Top;
        shell.BorderBrush = Line;
        shell.BorderThickness = new Thickness(1);
        shell.ClipToBounds = true;
        Window.Content = shell;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CompactHeight - 2) });
        layout.RowDefinitions.Add(new RowDefinition());
        shell.Child = layout;

        var compact = new DockPanel { LastChildFill = true };
        activityGlyph.Children.Add(Icon("M1,8 L4,8 L6,3 L9,13 L12,6 L14,8 L17,8", 17, Blue));
        for (int i = 0; i < 4; i++)
        {
            var bar = Surface(Blue, 1.2);
            bar.Width = 2.5;
            bar.Height = 11;
            bar.Margin = new Thickness(1, 0, 0, 0);
            bar.RenderTransformOrigin = new Point(0.5, 0.5);
            bar.RenderTransform = new ScaleTransform(1, 0.45);
            playbackBars.Children.Add(bar);
        }
        playbackBars.Visibility = Visibility.Collapsed;
        activityGlyph.Children.Add(playbackBars);
        Window.IsVisibleChanged += (_, _) => UpdatePlaybackGlyph();
        compactActivity.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(activityGlyph, Dock.Left);
        DockPanel.SetDock(compactTime, Dock.Left);
        countryBadge.Visibility = Visibility.Collapsed;
        countryBadge.Padding = new Thickness(3, 2, 3, 2);
        countryBadge.Margin = new Thickness(7, 0, 0, 0);
        countryBadge.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(countryBadge, Dock.Right);
        compact.Children.Add(activityGlyph);
        compact.Children.Add(compactTime);
        compact.Children.Add(countryBadge);
        compact.Children.Add(compactActivity);
        var header = Button(compact, "Раскрыть или свернуть Dynamic Island", Toggle, Brushes.Transparent, 12);
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        header.VerticalAlignment = VerticalAlignment.Stretch;
        layout.Children.Add(header);

        panel = new Grid { Margin = new Thickness(14, 6, 14, 12), Visibility = Visibility.Collapsed };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(panel, 1);
        layout.Children.Add(panel);

        previousButton = Button("Назад", () => ChangeTrack("previous"));
        playButton = Button("Пуск", () => ChangeTrack("toggle"));
        nextButton = Button("Дальше", () => ChangeTrack("next"));
        pauseButton = Button("Пауза", () => { focus.TogglePause(DateTimeOffset.UtcNow); RefreshClock(); });
        resetButton = Button("Сброс", () => { focus.Reset(); RefreshClock(); });
        clipToggle = Button("Включить", () => ClipboardToggleRequested?.Invoke());

        views = [BuildHome(), BuildMusic(), BuildScreenshots(), BuildActivity(), BuildClipboard(), BuildFocus()];
        string[] labels = ["Главная", "Музыка", "Снимки", "События", "Буфер", "Фокус"];
        tabs = new Button[labels.Length];
        var tabRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        for (int i = 0; i < labels.Length; i++)
        {
            var index = (IslandTab)i;
            tabs[i] = Button(labels[i], () => Select(index), Brushes.Transparent);
            tabs[i].Margin = new Thickness(2);
            tabRow.Children.Add(tabs[i]);
        }
        panel.Children.Add(Surface(Paint("#FF191923"), 11, tabRow));
        body.Margin = new Thickness(0, 9, 0, 0);
        Grid.SetRow(body, 1);
        panel.Children.Add(body);
        Select(IslandTab.Home);
        RefreshScreenshots();
        activity.Updated += RenderActivity;
        activity.Arrived += app => Flash($"●  {app}");
        prism.Updated += RefreshPrism;
        network.Changed += RenderNetwork;
        RenderNetwork();
        RefreshPrism();
        RenderClipboard();
        RenderMedia();
        RefreshClock();

        hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            if (!pinned && Window.IsVisible) SetExpanded(shell.IsMouseOver);
        };
        shell.MouseEnter += (_, _) => ScheduleHover();
        shell.MouseLeave += (_, _) => ScheduleHover();
        Window.Deactivated += (_, _) =>
        {
            pinned = false;
            if (!shell.IsMouseOver) SetExpanded(false);
        };
        Window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || !expanded) return;
            args.Handled = true;
            Collapse();
        };
    }

    private FrameworkElement BuildMusic()
    {
        var header = new DockPanel { Margin = new Thickness(12, 10, 12, 6) };
        var artwork = Surface(Gradient("#FF4F58D4", "#FF9F53A8"), 11,
            Icon("M5,15 L5,5 L16,2 L16,12 M5,15 C5,19 0,19 0,16 C0,14 3,13 5,15 M16,12 C16,16 11,16 11,13 C11,11 14,10 16,12", 23));
        artwork.Width = artwork.Height = 48;
        artwork.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(artwork, Dock.Left);
        header.Children.Add(artwork);
        var details = Stack(trackTitle, trackArtist);
        details.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(details);
        var controls = Row(previousButton, playButton, nextButton);
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        mediaHint.HorizontalAlignment = HorizontalAlignment.Center;
        mediaHint.Margin = new Thickness(0, 8, 0, 0);
        return Surface(Card, 16, Stack(header, controls, mediaProgress, mediaHint));
    }

    private FrameworkElement BuildClipboard()
    {
        var head = new DockPanel { Margin = new Thickness(1, 0, 1, 6) };
        DockPanel.SetDock(clipToggle, Dock.Right);
        head.Children.Add(clipToggle);
        head.Children.Add(clipHint);
        var layout = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        layout.Children.Add(head);
        layout.Children.Add(new ScrollViewer { Content = clipItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return layout;
    }

    private FrameworkElement BuildFocus()
    {
        var controls = Row(
            Button("5 мин", () => StartFocus(5)), Button("15 мин", () => StartFocus(15)),
            Button("25 мин", () => StartFocus(25)), pauseButton, resetButton);
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        focusTime.HorizontalAlignment = focusStatus.HorizontalAlignment = HorizontalAlignment.Center;
        focusStatus.Margin = new Thickness(0, 0, 0, 9);
        var layout = Stack(focusTime, focusStatus, controls);
        layout.VerticalAlignment = VerticalAlignment.Center;
        return Surface(Card, 16, layout);
    }

    private static StackPanel Stack(params UIElement[] elements)
    {
        var stack = new StackPanel();
        foreach (var element in elements) stack.Children.Add(element);
        return stack;
    }

    private static StackPanel Row(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(3, 0, 3, 0);
            row.Children.Add(button);
        }
        return row;
    }

    internal void Select(IslandTab tab)
    {
        selected = tab;
        int index = (int)tab;
        for (int i = 0; i < tabs.Length; i++) tabs[i].Background = i == index ? Chip : Brushes.Transparent;
        body.Content = views[index];
        if (tab == IslandTab.Screenshots) RefreshScreenshots();
        if (!expanded) return;
        MoveHeight();
        if (animate) Entrance(body, 7, 1);
    }

    private void ScheduleHover()
    {
        hover.Stop();
        if (!pinned) hover.Start();
    }

    internal void Toggle()
    {
        if (pinned) Collapse();
        else Open();
    }

    internal void Open()
    {
        hover.Stop();
        pinned = true;
        SetExpanded(true);
    }

    internal void Collapse()
    {
        hover.Stop();
        pinned = false;
        SetExpanded(false);
    }

    private void SetExpanded(bool value)
    {
        if (expanded == value) return;
        expanded = value;
        shell.CornerRadius = new CornerRadius(value ? 27 : 19);
        MoveHeight();
        panel.IsHitTestVisible = value;
        if (value)
        {
            panel.Visibility = Visibility.Visible;
            if (animate) Entrance(panel, -7, 0.975);
        }
        else if (animate)
        {
            var fade = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { if (!expanded) panel.Visibility = Visibility.Collapsed; };
            panel.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        else panel.Visibility = Visibility.Collapsed;
        UpdateWidth();
        if (!value) Collapsed?.Invoke();
    }

    private void MoveHeight()
    {
        double from = shell.Height;
        double to = !expanded ? CompactHeight : selected switch
        {
            IslandTab.Home => 337,
            IslandTab.Music => 243,
            IslandTab.Focus => 235,
            IslandTab.Clipboard => 280,
            _ => PanelHeight
        };
        shell.Height = to;
        Animate(shell, FrameworkElement.HeightProperty, from, to);
    }

    internal void Place(Rect area, bool desktopVisible)
    {
        double scale = Math.Min(1, Math.Min((area.Width - 12) / PanelWidth, (area.Height - 40) / PanelHeight));
        scale = Math.Max(0.1, scale);
        Window.Width = PanelWidth * scale;
        Window.Height = PanelHeight * scale;
        Window.Left = area.Left + (area.Width - Window.Width) / 2;
        Window.Top = area.Top + (desktopVisible ? Desktop.BarHeight + 4 : 5);
        shell.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
    }

    private void UpdateWidth()
    {
        double to = expanded ? PanelWidth : eventLabel is not null ? 330 : media.Current is { Playing: true } ? 302 : 190;
        if (targetWidth == to) return;
        targetWidth = to;
        double from = shell.Width;
        shell.Width = to;
        Animate(shell, FrameworkElement.WidthProperty, from, to);
    }

    private void Animate(DependencyObject element, DependencyProperty property, double from, double to)
    {
        if (!animate || element is not UIElement visual) return;
        visual.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(265))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    internal void RefreshClock(bool refreshBattery = false)
    {
        if (refreshBattery) battery = Native.BatteryText();
        var now = DateTimeOffset.UtcNow;
        if (focus.Tick(now))
        {
            SystemSounds.Asterisk.Play();
            SetEvent("✓  Фокус завершён", now);
        }
        if (eventLabel is not null && now >= eventUntil)
        {
            eventLabel = null;
            UpdateWidth();
        }
        int seconds = (int)Math.Ceiling(focus.Remaining(now).TotalSeconds);
        string countdown = $"{seconds / 60:00}:{seconds % 60:00}";
        compactTime.Text = DateTime.Now.ToString("HH:mm");
        focusTime.Text = countdown;
        focusStatus.Text = focus.Finished ? "Сессия завершена" : focus.Running ? "Идёт сессия фокуса" :
            focus.Paused ? "На паузе" : "Выберите длительность";
        overviewFocus.Text = focus.Finished ? "Сессия завершена" : focus.Running || focus.Paused ? countdown : "Запустить таймер";
        pauseButton.IsEnabled = focus.Running || focus.Paused;
        ((TextBlock)pauseButton.Content).Text = focus.Paused ? "Дальше" : "Пауза";
        resetButton.IsEnabled = focus.Running || focus.Paused || focus.Finished;
        compactActivity.Text = eventLabel ?? (focus.Finished ? "✓  Фокус завершён" : focus.Running || focus.Paused ? $"◷  {countdown}" :
            media.Current is { Playing: true } ? $"♫  {trackTitle.Text}" : battery ?? "LUMA");
        UpdateMediaProgress(now);
    }

    private void UpdateMediaProgress(DateTimeOffset now)
    {
        if (media.Current is not { } track || track.Duration <= TimeSpan.Zero || track.Duration > TimeSpan.FromDays(1))
        {
            mediaProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var elapsed = track.Playing && track.ReportedAt.Year > 2000 && track.ReportedAt <= now
            ? track.Position + (now - track.ReportedAt) : track.Position;
        mediaProgress.Value = Math.Clamp(elapsed.TotalSeconds / track.Duration.TotalSeconds, 0, 1);
        mediaProgress.Visibility = Visibility.Visible;
    }

    internal void Flash(string message)
    {
        SetEvent(Label(message, 64), DateTimeOffset.UtcNow);
        RefreshClock();
    }

    private void SetEvent(string message, DateTimeOffset now)
    {
        eventLabel = message;
        eventUntil = now.AddSeconds(6);
        UpdateWidth();
        if (animate && Window.IsVisible) compactActivity.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(300)));
    }

    private void StartFocus(int minutes)
    {
        focus.Start(TimeSpan.FromMinutes(minutes), DateTimeOffset.UtcNow);
        RefreshClock();
    }

    internal async void RefreshMedia()
    {
        await media.RefreshAsync();
        RenderMedia();
    }

    private async void ChangeTrack(string action)
    {
        bool changed = await media.ControlAsync(action);
        await media.RefreshAsync();
        RenderMedia();
        if (!changed) mediaHint.Text = "Действие недоступно в текущем плеере";
    }

    private void RenderMedia()
    {
        var track = media.Current;
        string title = Label(track?.Title ?? "", 100);
        trackTitle.Text = track is null ? "Нет активного плеера" : title.Length == 0 ? "Медиасессия Windows" : title;
        trackArtist.Text = track is null ? "Включите музыку в приложении или браузере" : Label(track.Artist, 100);
        overviewMusic.Text = trackTitle.Text;
        previousButton.IsEnabled = track?.Previous == true;
        playButton.IsEnabled = track?.Toggle == true;
        nextButton.IsEnabled = track?.Next == true;
        ((TextBlock)playButton.Content).Text = track?.Playing == true ? "Пауза" : "Пуск";
        mediaHint.Text = track?.Playing == true ? "Сейчас играет" : "Системный медиаплеер Windows";
        UpdatePlaybackGlyph();
        UpdateWidth();
        RefreshClock();
    }

    private void UpdatePlaybackGlyph()
    {
        bool playing = animate && Window.IsVisible && media.Current?.Playing == true;
        if (playingAnimation == playing) return;
        playingAnimation = playing;
        activityGlyph.Children[0].Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        playbackBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < playbackBars.Children.Count; i++)
        {
            var scale = (ScaleTransform)playbackBars.Children[i].RenderTransform;
            var pulse = playing ? new DoubleAnimation(0.28, 1, TimeSpan.FromMilliseconds(210 + i * 87))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            } : null;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
    }

    internal void SetClipboardEnabled(bool enabled)
    {
        clipboardEnabled = enabled;
        if (!enabled) history.Clear();
        RenderClipboard();
    }

    internal void CaptureClipboard()
    {
        if (!clipboardEnabled) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false) ||
                data.GetDataPresent("CanIncludeInClipboardHistory", false)) return;
            if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 4000) return;
            if (history.Count > 0 && history[0] == text) return;
            history.Remove(text);
            history.Insert(0, text);
            if (history.Count > 10) history.RemoveAt(10);
            RenderClipboard();
        }
        catch (ExternalException) { clipHint.Text = "Буфер временно недоступен"; }
    }

    private void RenderClipboard()
    {
        ((TextBlock)clipToggle.Content).Text = clipboardEnabled ? "Выключить и очистить" : "Включить";
        clipHint.Text = clipboardEnabled ? $"{history.Count} из 10 • только в памяти" : "История выключена";
        clipItems.Children.Clear();
        if (history.Count == 0)
        {
            var empty = Text(clipboardEnabled ? "Скопируйте текст, чтобы добавить его сюда" : "Включите историю для этой сессии", 12, Muted);
            empty.Margin = new Thickness(10, 12, 0, 0);
            clipItems.Children.Add(empty);
        }
        foreach (var text in history)
        {
            string preview = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            var entry = Text(preview, 12);
            entry.MaxWidth = 318;
            var button = Button(entry, "Скопировать запись", () => CopyEntry(text), Card);
            button.Margin = new Thickness(0, 0, 0, 4);
            clipItems.Children.Add(button);
        }
    }

    private void CopyEntry(string text)
    {
        try
        {
            Clipboard.SetText(text);
            clipHint.Text = "Скопировано";
        }
        catch (ExternalException) { clipHint.Text = "Буфер временно недоступен"; }
    }
}
