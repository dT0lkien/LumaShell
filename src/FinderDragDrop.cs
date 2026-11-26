using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LumaShell;

internal sealed partial class FinderWindow
{
    private Point? dragStart;
    private DropHighlight? dropHighlight;
    private string? dropDestination;
    private DragDropEffects highlightedEffect;

    private void ConfigureFileDrag()
    {
        list.PreviewMouseLeftButtonDown += (_, args) =>
            dragStart = RowUnder(args.OriginalSource) is null ? null : args.GetPosition(list);
        list.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        list.PreviewMouseMove += StartFileDrag;
        AllowDrop = true;
        PreviewDragEnter += UpdateDropTarget;
        PreviewDragOver += UpdateDropTarget;
        PreviewDragLeave += (_, _) => ClearDropTarget();
        PreviewDrop += DropFiles;
    }

    private void StartFileDrag(object sender, MouseEventArgs args)
    {
        if (copying || args.LeftButton != MouseButtonState.Pressed) { dragStart = null; return; }
        if (dragStart is not { } start) return;
        var pointer = args.GetPosition(list);
        if (Math.Abs(pointer.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pointer.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragStart = null;
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        args.Handled = true;
        DragDrop.DoDragDrop(list, FinderFiles.FileDropData(paths), DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void UpdateDropTarget(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (DestinationUnderPointer(args) is not { } target || ReadDropPaths(args.Data, target.Path).Length == 0)
        {
            ClearDropTarget();
            return;
        }
        args.Effects = DropEffect(args.KeyStates, args.AllowedEffects);
        if (args.Effects == DragDropEffects.None) { ClearDropTarget(); return; }
        if (dropHighlight?.AdornedElement == target.Element && dropDestination == target.Path && highlightedEffect == args.Effects)
            return;
        ClearDropTarget();
        if (AdornerLayer.GetAdornerLayer(target.Element) is not { } layer) return;
        dropHighlight = new DropHighlight(target.Element);
        layer.Add(dropHighlight);
        dropDestination = target.Path;
        highlightedEffect = args.Effects;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.Path));
        if (name.Length == 0) name = target.Path;
        status.Text = args.Effects == DragDropEffects.Move ? $"Переместить в «{name}»" : $"Копировать в «{name}»";
    }

    private void ClearDropTarget()
    {
        if (dropHighlight is not { } highlight) return;
        AdornerLayer.GetAdornerLayer(highlight.AdornedElement)?.Remove(highlight);
        dropHighlight = null;
        dropDestination = null;
        UpdateStatus();
    }

    private async void DropFiles(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var effect = DropEffect(args.KeyStates, args.AllowedEffects);
        args.Effects = DragDropEffects.None;
        ClearDropTarget();
        if (effect == DragDropEffects.None || DestinationUnderPointer(args) is not { } target ||
            ReadDropPaths(args.Data, target.Path) is not { Length: > 0 } paths) return;

        bool move = effect == DragDropEffects.Move;
        if (move)
        {
            // The destination moves the files itself. The source must not delete them again.
            try { args.Data.SetData("Performed DropEffect", new MemoryStream(new byte[4]), false); }
            catch (Exception error) when (error is ExternalException or InvalidOperationException or NotSupportedException or NotImplementedException) { }
        }
        else args.Effects = DragDropEffects.Copy;
        await TransferFiles(paths, target.Path, move);
    }

    private (FrameworkElement Element, string Path)? DestinationUnderPointer(DragEventArgs args)
    {
        if (copying || load is not null || folder is null) return null;
        if (InputHitTest(args.GetPosition(this)) is not DependencyObject hit) return null;
        foreach (var (button, path) in placeButtons)
            if (hit == button || button.IsAncestorOf(hit)) return (button, path);
        foreach (var child in pathBar.Children)
            if (child is Button { Tag: string path } button && (hit == button || button.IsAncestorOf(hit)))
                return (button, path);
        if (hit != list && !list.IsAncestorOf(hit)) return null;
        if (RowUnder(hit) is { Content: FinderEntry { IsDirectory: true } item } row) return (row, item.Path);
        return (list, folder);
    }

    internal static string[] ReadDropPaths(IDataObject data, string target)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, false) ||
                data.GetData(DataFormats.FileDrop, false) is not string[] incoming) return [];
            string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            if (incoming.Any(path => !Path.IsPathFullyQualified(path))) return [];
            var paths = incoming.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Any(path => destination.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))) return [];
            return paths.Where(path => !string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or ExternalException or InvalidOperationException or IOException)
        { return []; }
    }

    internal static DragDropEffects DropEffect(DragDropKeyStates keys, DragDropEffects allowed)
    {
        if ((keys & DragDropKeyStates.ShiftKey) != 0 && (allowed & DragDropEffects.Move) != 0)
            return DragDropEffects.Move;
        if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        return (keys & DragDropKeyStates.ControlKey) == 0 ? allowed & DragDropEffects.Move : DragDropEffects.None;
    }

    private sealed class DropHighlight : Adorner
    {
        private static readonly Brush Tint = Visuals.Paint("#168CC4FF");
        private static readonly Pen Outline = new(Accent, 1.5);

        internal DropHighlight(UIElement element) : base(element) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext drawing)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width < 4 || size.Height < 4) return;
            drawing.DrawRoundedRectangle(Tint, Outline, new Rect(1, 1, size.Width - 2, size.Height - 2), 6, 6);
        }
    }
}
