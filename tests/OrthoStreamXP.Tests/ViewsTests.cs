using System.Windows;

namespace OrthoStreamXP.Tests;

/// <summary>The window's pages load with its theme: a XAML mistake shows here, not as an error box at start.</summary>
public sealed class ViewsTests
{
    [Fact]
    public void Every_page_loads_with_the_theme()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/orthostreamxp;component/Gui/Theme.xaml") });
                foreach (Type view in new[] { typeof(OrthoStreamXP.Gui.HomeView), typeof(OrthoStreamXP.Gui.SettingsView), typeof(OrthoStreamXP.Gui.CacheView), typeof(OrthoStreamXP.Gui.PackagesView), typeof(OrthoStreamXP.Gui.BuildSceneryView), typeof(OrthoStreamXP.Gui.AirportsView), typeof(OrthoStreamXP.Gui.LogView) })
                {
                    var page = (FrameworkElement)Activator.CreateInstance(view, nonPublic: true)!;
                    page.Measure(new Size(1200, 900));
                    page.Arrange(new Rect(0, 0, 1200, 900));
                }

                // The Build Scenery page with its model: its map drawn with picked and built tiles, in light and dark.
                using (var tree = new TempTree())
                {
#pragma warning disable WPF0001
                    foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                    {
                        app.ThemeMode = theme;
#pragma warning restore WPF0001
                        var model = new OrthoStreamXP.Gui.BuildSceneryViewModel(new OrthoStreamXP.Gui.MountController(), () => null, () => tree.Root, _ => { },
                            new OrthoStreamXP.Setup.SceneryBuildPreferencesStore(tree.PathOf("scenery-build.json")));
                        OrthoGlobalXP.Geo.TileCoordinate picked = new(50, 14), built = new(50, 15);
                        model.Map.SetAvailable([picked, built]);
                        model.Map.Select([picked, built], true);
                        model.Map.SetBuilt([built]);
                        var page = new OrthoStreamXP.Gui.BuildSceneryView { DataContext = model };
                        page.Measure(new Size(1000, 900));
                        page.Arrange(new Rect(0, 0, 1000, 900));
                        page.UpdateLayout();
                        new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32).Render(page);
                        Assert.Equal("2 tiles picked", model.SelectionText);
                        Assert.True(model.IsPackagesFolder);
                    }
                }

                // The Build tab sits in the Packages page's scroller: its log following its last line does not scroll the page.
                using (var tree = new TempTree())
                {
                    var model = new OrthoStreamXP.Gui.BuildSceneryViewModel(new OrthoStreamXP.Gui.MountController(), () => null, () => tree.Root, _ => { },
                        new OrthoStreamXP.Setup.SceneryBuildPreferencesStore(tree.PathOf("scenery-build.json")));
                    var scroller = new System.Windows.Controls.ScrollViewer { Content = new OrthoStreamXP.Gui.BuildSceneryView { DataContext = model } };
                    var window = new Window
                    {
                        Content = scroller, Width = 1000, Height = 500, Left = -20000, Top = -20000,
                        WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    };
                    window.Show();
                    try
                    {
                        for (int i = 0; i < 50; i++) model.BuildLog.Add($"line {i}");
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        Assert.True(scroller.ScrollableHeight > 0);
                        Assert.Equal(0, scroller.VerticalOffset);
                    }
                    finally
                    {
                        window.Close();
                    }
                }

                // The update window, with notes and without.
                foreach (string? notes in new[] { "## What's new\n\n- **Feature.** What it does.\n\nA paragraph.", null })
                {
                    var offer = new OrthoStreamXP.Gui.UpdateOffer("9.9.9", "1.0.0", OrthoStreamXP.Gui.ReleaseNotes.Parse(notes), "https://example.com");
                    var dialog = new OrthoStreamXP.Gui.UpdateDialog(offer);
                    var content = (FrameworkElement)dialog.Content;
                    content.Measure(new Size(600, 560));
                    content.Arrange(new Rect(0, 0, 600, 560));
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
