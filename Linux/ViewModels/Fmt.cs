using System.Globalization;
using Avalonia.Data.Converters;

namespace FrameCastStudio.Linux.ViewModels;

/// <summary>Formate un nombre dans le XAML : <c>Converter={x:Static vm:Fmt.Instance}, ConverterParameter='0.0 dB'</c>.
/// Le paramètre est un format numérique .NET personnalisé (les lettres non spéciales sont recopiées telles quelles).</summary>
public sealed class Fmt : IValueConverter
{
    public static readonly Fmt Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string f = parameter as string ?? "0.##";
        return value is IFormattable x ? x.ToString(f, culture) : value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
