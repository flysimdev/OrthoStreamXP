using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using OrthoGlobalXP.Geo;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The Packages page's Build tab: wires the map to the page's model (the tile under the mouse, the parts of the map to show), the
/// zoom buttons, the region search's drop-down list, and the log following its last line. The model outlives the view (a page is made again each time it is
/// shown), so the view listens to it only while it is loaded.
/// </summary>
internal sealed partial class BuildSceneryView
{
    private const double ZoomStep = 1.6, PickedMaxPixelsPerDegree = 400;
    private BuildSceneryViewModel? _model;
    private Window? _window;
    private ScrollViewer? _pageScroller;
    private bool _choosingRegion;

    public BuildSceneryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
        PreviewMouseDown += OnPagePressed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        _pageScroller = FindPageScroller();
        if (_pageScroller is not null) _pageScroller.ScrollChanged += OnPageScrolled;
        _model = DataContext as BuildSceneryViewModel;
        if (_model is null) return;
        MapView.HoverChanged += OnHoverChanged;
        _model.FrameRequested += OnFrameRequested;
        _model.BuildLog.CollectionChanged += OnLogChanged;
        _model.Regions.PropertyChanged += OnRegionsChanged;
        // Start over the picked tiles, or over Europe.
        if (!ShowPicked()) MapView.CenterOn(50, 12, 10);
        ScrollLogToEnd();
    }

    private void Detach()
    {
        RegionPopup.IsOpen = false;
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
        if (_pageScroller is not null) _pageScroller.ScrollChanged -= OnPageScrolled;
        _pageScroller = null;
        if (_model is null) return;
        MapView.HoverChanged -= OnHoverChanged;
        _model.FrameRequested -= OnFrameRequested;
        _model.BuildLog.CollectionChanged -= OnLogChanged;
        _model.Regions.PropertyChanged -= OnRegionsChanged;
        _model = null;
    }

    private void OnHoverChanged(object? sender, TileCoordinate? tile) => _model?.SetHover(tile);

    private void OnFrameRequested(object? sender, MapFrame frame) => MapView.Frame(frame.West, frame.South, frame.East, frame.North);

    private void OnZoomIn(object sender, RoutedEventArgs e) => MapView.ZoomBy(ZoomStep);

    private void OnZoomOut(object sender, RoutedEventArgs e) => MapView.ZoomBy(1 / ZoomStep);

    private void OnShowPicked(object sender, RoutedEventArgs e) => ShowPicked();

    /// <summary>Shows the picked tiles (a few hundred pixels per degree at most); false when none are picked.</summary>
    private bool ShowPicked()
    {
        if (_model?.Map.Selected is not { Count: > 0 } picked) return false;
        MapView.Frame(picked.Min(t => t.Lon), picked.Min(t => t.Lat), picked.Max(t => t.Lon) + 1, picked.Max(t => t.Lat) + 1, PickedMaxPixelsPerDegree);
        return true;
    }

    private void OnRegionBoxFocused(object sender, KeyboardFocusChangedEventArgs e) => OpenRegions();

    private void OnRegionBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => RegionPopup.IsOpen = false;

    /// <summary>A click in the box opens the list again after it was closed while the box kept the focus.</summary>
    private void OnRegionBoxClicked(object sender, MouseButtonEventArgs e) => OpenRegions();

    private void OnRegionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_choosingRegion && RegionBox.IsKeyboardFocused) OpenRegions();
    }

    /// <summary>The search runs a moment after the typing: the list opens (or closes) once its matches are in.</summary>
    private void OnRegionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegionSearch.Matches) && !_choosingRegion && RegionBox.IsKeyboardFocused) OpenRegions();
    }

    /// <summary>Up and Down move through the list, Enter takes the area (or, with the list closed, picks its tiles), Escape closes the list.</summary>
    private void OnRegionBoxKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                if (!RegionPopup.IsOpen) OpenRegions();
                else MoveRegionSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter when RegionPopup.IsOpen && RegionList.SelectedItem is RegionItem item:
                ChooseRegion(item);
                e.Handled = true;
                break;
            case Key.Enter when !RegionPopup.IsOpen && _model?.PickRegion.CanExecute(null) == true:
                _model.PickRegion.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when RegionPopup.IsOpen:
                RegionPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnRegionClicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RegionList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: RegionItem item })
            ChooseRegion(item);
    }

    /// <summary>A click anywhere but in the search or its list closes the list.</summary>
    private void OnPagePressed(object sender, MouseButtonEventArgs e)
    {
        if (RegionPopup.IsOpen && !RegionBox.IsMouseOver && !RegionList.IsMouseOver) RegionPopup.IsOpen = false;
    }

    /// <summary>The list stays where it opened, so it closes when the page scrolls away under it.</summary>
    private void OnPageScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource == _pageScroller && e.VerticalChange != 0) RegionPopup.IsOpen = false;
    }

    /// <summary>The scroller of the page the tab is on.</summary>
    private ScrollViewer? FindPageScroller()
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroller) return scroller;
        return null;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => RegionPopup.IsOpen = false;

    private void OpenRegions() => RegionPopup.IsOpen = _model?.Regions.Matches.Count > 0;

    private void MoveRegionSelection(int step)
    {
        if (RegionList.Items.Count == 0) return;
        RegionList.SelectedIndex = Math.Clamp(RegionList.SelectedIndex + step, 0, RegionList.Items.Count - 1);
        RegionList.ScrollIntoView(RegionList.SelectedItem);
    }

    /// <summary>Puts the area's name in the box and closes the list, keeping the focus in the box.</summary>
    private void ChooseRegion(RegionItem item)
    {
        _choosingRegion = true;
        try
        {
            _model?.Regions.Choose(item);
        }
        finally
        {
            _choosingRegion = false;
        }
        RegionPopup.IsOpen = false;
        RegionBox.CaretIndex = RegionBox.Text.Length;
        RegionBox.Focus();
    }

    private void OnLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        Shell.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    /// <summary>
    /// Follows the last line once the list has taken in the change: this handler can run before the list's own, and
    /// scrolling a list in the middle of a change leaves it inconsistent.
    /// </summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollLogToEnd();
    }

    /// <summary>
    /// Following the last line scrolls the log only: the log's own scroller passes the request on to its parents, and the
    /// Packages page would otherwise scroll down to the log each time a line comes in.
    /// </summary>
    private void OnLogBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    private void ScrollLogToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (LogLines.Items.Count > 0) LogLines.ScrollIntoView(LogLines.Items[^1]);
    });
}
