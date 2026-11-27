using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MediaRecorder.App.Converters;

/// <summary>Nicht-null bzw. nicht-leerer String -> true (für InfoBar.IsOpen).</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>null -> Visible (Platzhalter „Keine Vorschau“).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
