using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace LumaShell;

internal static class WindowControls
{
    private static readonly ControlTemplate Template = (ControlTemplate)Application.LoadComponent(
        new Uri("/LumaShell;component/CaptionButton.xaml", UriKind.Relative));
    private static readonly Geometry CloseMark = Geometry.Parse("M4.5,4.5 L9.5,9.5 M9.5,4.5 L4.5,9.5");
    private static readonly Geometry MinimizeMark = Geometry.Parse("M3.8,7 H10.2");
    private static readonly Geometry EnlargeMark = Geometry.Parse("M4,7 V4 H7 M10,7 V10 H7");
    private static readonly Geometry RestoreMark = Geometry.Parse("M4,7 H7 V4 M10,8 H8 V10");

    internal static bool HasFocus => System.Windows.Input.Keyboard.FocusedElement is Button button && button.Template == Template;

    internal static Button Close(Window owner, string label) =>
        Make(label, "#FFF46663", CloseMark, owner.Close);

    internal static Button Minimize(Window owner) =>
        Make("Свернуть окно", "#FFF3B84C", MinimizeMark, () => owner.WindowState = WindowState.Minimized);

    internal static Button Zoom(Window owner)
    {
        var button = Make("Развернуть окно", "#FF48C482", EnlargeMark, () =>
            owner.WindowState = owner.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        owner.StateChanged += (_, _) =>
        {
            bool maximized = owner.WindowState == WindowState.Maximized;
            button.Content = maximized ? RestoreMark : EnlargeMark;
            string label = maximized ? "Восстановить размер окна" : "Развернуть окно";
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        };
        return button;
    }

    private static Button Make(string label, string color, Geometry mark, Action click)
    {
        var button = new Button
        {
            Width = 24, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Template = Template, Content = mark, Background = Brushes.Transparent, Foreground = Visuals.Paint(color),
            OverridesDefaultStyle = true, FocusVisualStyle = null, ToolTip = label
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        AutomationProperties.SetName(button, label);
        button.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (button.IsMouseCaptured && !new Rect(button.RenderSize).Contains(args.GetPosition(button)))
            {
                button.ReleaseMouseCapture();
                args.Handled = true;
            }
        };
        button.Click += (_, _) => click();
        return button;
    }
}
