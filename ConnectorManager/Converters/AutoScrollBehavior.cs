using System.Windows;
using System.Windows.Controls;

namespace ConnectorManager.Converters;

/// <summary>
/// Attached behavior that auto-scrolls a TextBox to the end whenever its text changes.
/// Usage: converters:AutoScrollBehavior.IsEnabled="True"
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox)
        {
            if ((bool)e.NewValue)
            {
                textBox.TextChanged += OnTextChanged;
            }
            else
            {
                textBox.TextChanged -= OnTextChanged;
            }
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
