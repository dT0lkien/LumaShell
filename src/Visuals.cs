using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LumaShell;

internal static class Visuals
{
    internal static readonly Brush White = Paint("#F7F8FC");
    internal static readonly Brush Muted = Paint("#A7A9BB");
    internal static readonly Brush Blue = Paint("#9FB8FF");
    internal static readonly Brush Line = Paint("#26FFFFFF");
    internal static readonly Brush Card = Paint("#FF1A1A23");
    internal static readonly Brush Chip = Paint("#22FFFFFF");
    private static readonly ControlTemplate ButtonTemplate = CreateButtonTemplate();

    internal static string Label(string value, int limit)
    {
        var result = new StringBuilder(Math.Min(value.Length, limit));
        int marks = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
                marks = 2;
                continue;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.EnclosingMark) continue;
            if (result.Length + rune.Utf16SequenceLength > limit) { result.Append('…'); break; }
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (result.Length == 0 || ++marks > 2) continue;
            }
            else marks = 0;
            result.Append(rune.ToString());
        }
        return result.ToString().TrimEnd();
    }

    internal static SolidColorBrush Paint(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush Gradient(string start, string end) => new(
        (Color)ColorConverter.ConvertFromString(start), (Color)ColorConverter.ConvertFromString(end),
        new Point(0, 0), new Point(1, 1));

    internal static TextBlock Text(string value, double size = 13, Brush? color = null, bool bold = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? White,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    internal static Window Overlay(string title, double width, double height)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            UseLayoutRounding = true
        };
        window.SourceInitialized += (_, _) => Native.ExcludeFromTaskSwitching(new WindowInteropHelper(window).Handle);
        return window;
    }

    internal static Border Surface(Brush background, double radius, UIElement? content = null) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(radius),
        Child = content
    };

    internal static void Entrance(FrameworkElement element, double travel = -8, double startScale = 0.97)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var time = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(shift);
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.RenderTransform = transform;
        DoubleAnimation Ease(double from, double to) => new(from, to, time) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(startScale, 1));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(startScale, 1));
        shift.BeginAnimation(TranslateTransform.YProperty, Ease(travel, 0));
        element.BeginAnimation(UIElement.OpacityProperty, Ease(0, 1));
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Paint("#38FFFFFF"), "Chrome"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.38));
        template.Triggers.Add(disabled);
        return template;
    }

    internal static Button Button(object content, string label, Action click, Brush? background = null, double padding = 9)
    {
        var button = new Button
        {
            Content = content,
            Template = ButtonTemplate,
            Background = background ?? Chip,
            Foreground = White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(padding, padding * 0.55, padding, padding * 0.55),
            MinHeight = 26,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = label
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    internal static Button Button(string label, Action click, Brush? background = null) =>
        Button(Text(label, 12, bold: true), label, click, background);

    internal static Path Icon(string geometry, double size, Brush? color = null) => new()
    {
        Data = Geometry.Parse(geometry),
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        Stroke = color ?? White,
        StrokeThickness = 1.7,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center
    };
}
