using System.Globalization;
using System.Windows.Data;
using TrainController.Integration.Presentation;

namespace TrainController.Wpf.Views.Controls;

/// <summary>Wraps a plain string as a neutral-tone <see cref="DisplayValue"/> for <see cref="KeyValueRow"/>.</summary>
public sealed class TextToDisplayValueConverter : IValueConverter
{
    public static TextToDisplayValueConverter Instance { get; } = new TextToDisplayValueConverter();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && text.Length > 0 ? new DisplayValue(text) : DisplayValue.Missing;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
