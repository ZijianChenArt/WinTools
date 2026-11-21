using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinTools;

public static class EdgeScrollbar
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(EdgeScrollbar), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (args.NewValue is true)
        {
            element.Loaded += OnLoaded;
            if (element.IsLoaded) Apply(element);
        }
        else element.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        if (element is not ScrollBar { Orientation: Orientation.Vertical } bar) return;
        bar.Loaded -= OnLoaded;
        // ScrollViewer animates IndicatorMode after input stops. The vertical template keeps
        // the native thumb and page buttons while leaving visibility to the scroll range.
        bar.Template = (ControlTemplate)Application.Current.Resources["EdgeVerticalScrollbarTemplate"];
    }
}
