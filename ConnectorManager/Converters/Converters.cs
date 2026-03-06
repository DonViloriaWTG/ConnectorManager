using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ConnectorManager.Converters;

/// <summary>
/// Converts a boolean to Visibility (true = Visible, false = Collapsed).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

/// <summary>
/// Converts an enum value to bool for RadioButton binding.
/// Usage: IsChecked="{Binding MyEnum, Converter={StaticResource EnumToBool}, ConverterParameter={x:Static local:MyEnum.Value}}"
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) ?? false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? parameter : DependencyProperty.UnsetValue;
    }
}
