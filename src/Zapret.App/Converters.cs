using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Zapret.Core.ViewModels;

namespace Zapret.App;

/// <summary>true, если значение перечисления совпадает с параметром (для вкладок и переключателей).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Пустая строка, null или false - скрыть элемент. С параметром "invert" - наоборот.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || value is string s && s.Length == 0 || value is false;
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Цвет результата проверки: зелёный - работает, жёлтый - частично, красный - нет.</summary>
public sealed class CheckKindToBrushConverter : IValueConverter
{
    public Brush Good { get; set; } = Brushes.LimeGreen;
    public Brush Partial { get; set; } = Brushes.Orange;
    public Brush Bad { get; set; } = Brushes.IndianRed;
    public Brush Neutral { get; set; } = Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CheckKind.Good => Good,
        CheckKind.Partial => Partial,
        CheckKind.Bad => Bad,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Короткие русские подписи для режимов и результатов проверки.</summary>
public sealed class RuLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Zapret.Core.GameFilterMode.Disabled => "выкл",
        Zapret.Core.GameFilterMode.All => "TCP+UDP",
        Zapret.Core.GameFilterMode.Tcp => "TCP",
        Zapret.Core.GameFilterMode.Udp => "UDP",
        Zapret.Core.IpsetMode.None => "нет",
        Zapret.Core.IpsetMode.Loaded => "список",
        Zapret.Core.IpsetMode.Any => "все",
        CheckKind.Good => "открывается",
        CheckKind.Partial => "частично",
        CheckKind.Bad => "не открывается",
        CheckKind.Checking => "проверяю…",
        CheckKind.None => "не проверялось",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>«general (ALT2)» → «ALT2», «general» → «General».</summary>
public sealed class StrategyTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? "";
        var open = name.IndexOf('(');
        var close = name.LastIndexOf(')');
        return open >= 0 && close > open ? name[(open + 1)..close] : "General";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
