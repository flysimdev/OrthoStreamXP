using System.Windows;
using System.Windows.Threading;

namespace OrthoStreamXP.Gui;

internal sealed partial class SetupView
{
    public SetupView() => InitializeComponent();

    /// <summary>The build opened: its top scrolled into sight once laid out.</summary>
    private void OnBuilderShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement builder)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => builder.BringIntoView(new Rect(0, 0, builder.ActualWidth, 1)));
    }
}
