using System;
using System.Globalization;
using System.Windows.Data;

namespace NetDocsImporter.App;

/// <summary>
/// Converts boolean values to their inverse for WPF bindings.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <summary>
    /// Inverts a boolean value during forward binding conversion.
    /// </summary>
    /// <param name="value">Source binding value.</param>
    /// <param name="targetType">Target type requested by the binding engine.</param>
    /// <param name="parameter">Optional converter parameter.</param>
    /// <param name="culture">Culture information.</param>
    /// <returns>Inverted boolean value when input is boolean; otherwise the original value.</returns>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : value;
    }

    /// <summary>
    /// Inverts a boolean value during reverse binding conversion.
    /// </summary>
    /// <param name="value">Target binding value.</param>
    /// <param name="targetType">Source type requested by the binding engine.</param>
    /// <param name="parameter">Optional converter parameter.</param>
    /// <param name="culture">Culture information.</param>
    /// <returns>Inverted boolean value when input is boolean; otherwise the original value.</returns>
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : value;
    }
}
