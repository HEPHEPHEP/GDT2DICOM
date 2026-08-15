using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Gdt2Dicom.Gui;

/// <summary>Kehrt einen Bool-Wert um – für „Feld nur aktiv, wenn Häkchen nicht gesetzt“.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>Färbt den Punkt in der Fußleiste: offene Änderungen gegenüber gespeichertem Zustand.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter PendingSaved = new()
    {
        TrueBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
        FalseBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D))
    };

    public Brush TrueBrush { get; init; } = Brushes.Orange;
    public Brush FalseBrush { get; init; } = Brushes.Green;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
