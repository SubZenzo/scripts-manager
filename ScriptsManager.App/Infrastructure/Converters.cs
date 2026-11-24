using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScriptsManager.App.Infrastructure;

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Collapsed для null, пустой строки и пустой коллекции; Invert — наоборот.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => s.Length == 0,
            bool b => !b,
            ICollection c => c.Count == 0,
            _ => false,
        };
        return empty != Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Значение ячейки результата: даты в ISO без долей секунды, большие числа с пробелами между разрядами.</summary>
/// <remarks>Только для показа: копирование и выгрузка берут исходные значения.</remarks>
public sealed class ResultCellConverter : IValueConverter
{
    public static readonly ResultCellConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null or DBNull => "NULL",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float =>
            GroupDigits(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)),
        _ => value,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // Делим только целую часть и только от пяти цифр (1234 — как есть, 12 345); экспоненту не трогаем.
    public static string GroupDigits(string number)
    {
        var start = number.StartsWith('-') ? 1 : 0;
        var end = start;
        while (end < number.Length && char.IsAsciiDigit(number[end]))
            end++;
        if (end - start < 5 || number.IndexOfAny(['E', 'e']) >= 0)
            return number;

        var sb = new System.Text.StringBuilder(number.Length + (end - start) / 3);
        sb.Append(number, 0, start);
        for (var i = start; i < end; i++)
        {
            if (i > start && (end - i) % 3 == 0)
                sb.Append(' ');
            sb.Append(number[i]);
        }
        return sb.Append(number, end, number.Length - end).ToString();
    }
}
