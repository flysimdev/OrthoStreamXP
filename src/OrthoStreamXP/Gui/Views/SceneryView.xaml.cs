using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The X-Plane Custom Scenery page. A row is dragged to a new place (anywhere on it but its switch): a picture of it follows
/// the pointer, the other rows slide apart where it would land, and the page scrolls while the pointer is near its top or
/// bottom edge.
/// </summary>
internal sealed partial class SceneryView
{
    /// <summary>How close to the page's top or bottom edge a drag scrolls it.</summary>
    private const double ScrollEdge = 48;

    private SceneryPackRow? _pressed;
    private Point _pressedAt;
    /// <summary>Where in the row it was grabbed, so the ghost keeps that spot under the pointer.</summary>
    private Point _grab;
    private DragGhost? _ghost;
    private double _ghostLeft;
    private ReorderPreview? _preview;

    public SceneryView() => InitializeComponent();

    private SceneryViewModel? Model => DataContext as SceneryViewModel;

    /// <summary>
    /// "Show in list" on a row the search kept: the search is cleared and the page scrolls so the row, now highlighted, sits in
    /// the middle of the whole list, ready to be dragged.
    /// </summary>
    private void OnShowInList(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SceneryPackRow row || Model is not { } model) return;
        model.ShowInList(row);
        // Once the list has laid out every row again.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container) return;
            double top = container.TranslatePoint(default, Scroller).Y;
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + top - (Scroller.ActualHeight - container.ActualHeight) / 2);
        });
    }

    /// <summary>Esc empties the search box, which shows every package again.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = IsOnButton(e.OriginalSource as DependencyObject) ? null : (sender as FrameworkElement)?.DataContext as SceneryPackRow;
        _pressedAt = e.GetPosition(this);
        _grab = e.GetPosition(sender as IInputElement);
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } row || e.LeftButton != MouseButtonState.Pressed || Model is not { CanDrag: true }) return;
        Vector moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = null;
        int from = List.Items.IndexOf(row);
        if (List.ItemContainerGenerator.ContainerFromIndex(from) is not FrameworkElement container) return;

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(this);
        _ghost = new DragGhost(this, container); // pictured before the preview fades the row
        _ghostLeft = container.TranslatePoint(default, this).X;
        layer?.Add(_ghost);
        MoveGhost(e.GetPosition(this));
        _preview = new ReorderPreview(List, from);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(SceneryPackRow), row), DragDropEffects.Move);
        }
        finally
        {
            _preview?.Reset(animate: true); // not dropped on the list: everything slides back
            _preview = null;
            layer?.Remove(_ghost);
            _ghost = null;
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_preview is null || !e.Data.GetDataPresent(typeof(SceneryPackRow)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        _preview.Show(_preview.InsertIndex(e));
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_preview is not { } preview || e.Data.GetData(typeof(SceneryPackRow)) is not SceneryPackRow row) return;
        int insertAt = preview.InsertIndex(e);
        // The rows go back to their places at once and the list takes the new order in the same frame: no jump.
        preview.Reset(animate: false);
        _preview = null;
        Model?.Drop(row, insertAt);
    }

    /// <summary>Over the page but not over the list: nothing to drop on, so the gap closes.</summary>
    private void OnOutsideDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        _preview?.Show(null);
    }

    /// <summary>Moves the ghost with the pointer, and scrolls while it is near the page's top or bottom edge (the list can be hundreds long).</summary>
    private void OnScrollerPreviewDragOver(object sender, DragEventArgs e)
    {
        MoveGhost(e.GetPosition(this));
        double y = e.GetPosition(Scroller).Y;
        if (y < ScrollEdge) Scroller.LineUp();
        else if (y > Scroller.ActualHeight - ScrollEdge) Scroller.LineDown();
    }

    /// <summary>The ghost stays in the list's column and follows the pointer up and down.</summary>
    private void MoveGhost(Point pointer) => _ghost?.MoveTo(new Point(_ghostLeft, pointer.Y - _grab.Y));

    /// <summary>Whether a press started on the row's switch (or another button), which keeps its click.</summary>
    private static bool IsOnButton(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is ButtonBase) return true;
        return false;
    }
}
