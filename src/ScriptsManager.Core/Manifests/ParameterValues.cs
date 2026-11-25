using System.Globalization;
using System.Text.RegularExpressions;

namespace ScriptsManager.Core.Manifests;

public static partial class ParameterValues
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
        "dd.MM.yyyy", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss",
    ];

    /// <summary>
    /// Проверяет значения параметра из формы или пресета: обязательность, количество, формат.
    /// Пустые строки считаются незаданным значением.
    /// </summary>
    public static string? ValidateField(ScriptParameter parameter, IReadOnlyList<string> values)
    {
        var set = NonEmpty(values);
        if (set.Count == 0)
            return parameter.Required ? "Обязательный параметр" : null;
        if (set.Count > 1 && !parameter.Multi)
            return "Допустимо только одно значение";

        foreach (var value in set)
        {
            if (Validate(parameter, value.Trim()) is { } error)
                return error;
        }

        return null;
    }

    /// <summary>
    /// Значение для передачи в скрипт: null — не задано; long, decimal, bool, DateTime, Guid, string;
    /// для списка с multi — string[], для range — long[]. Выражения дат вычисляются относительно <paramref name="now"/>.
    /// </summary>
    public static object? Resolve(ScriptParameter parameter, IReadOnlyList<string> values, DateTime now)
    {
        if (ValidateField(parameter, values) is { } error)
            throw new ArgumentException($"Параметр «{parameter.Name}»: {error}", nameof(values));

        var set = NonEmpty(values);
        if (parameter.Multi)
            return set.Select(v => v.Trim()).ToArray();
        if (set.Count == 0)
            return null;

        var raw = set[0];
        var value = raw.Trim();
        return parameter.Type switch
        {
            ParameterType.Int => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            ParameterType.Decimal => TryParseDecimal(value, out var d) ? d : throw new InvalidOperationException(),
            ParameterType.Bool => bool.Parse(value),
            ParameterType.Date => Date(value, now).Date,
            ParameterType.DateTime => Date(value, now),
            ParameterType.Guid => Guid.Parse(value),
            ParameterType.Text => raw,
            ParameterType.Range => TryParseRange(value, out var numbers) is null ? numbers.ToArray() : throw new InvalidOperationException(),
            _ => value,
        };

        static DateTime Date(string text, DateTime now) =>
            TryEvaluateDate(text, now, out var date) ? date : throw new InvalidOperationException();
    }

    /// <summary>Значение даты в формате формы и пресетов.</summary>
    public static string FormatDate(DateTime value, bool withTime) =>
        value.ToString(withTime ? (value.Second == 0 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm:ss") : "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    private static List<string> NonEmpty(IReadOnlyList<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    /// <summary>Проверяет одно значение параметра; возвращает текст ошибки или null.</summary>
    public static string? Validate(ScriptParameter parameter, string value)
    {
        switch (parameter.Type)
        {
            case ParameterType.Int:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return $"«{value}» — не целое число";
                return CheckRange(parameter, l);
            case ParameterType.Decimal:
                if (!TryParseDecimal(value, out var d))
                    return $"«{value}» — не число (разделитель — точка)";
                return CheckRange(parameter, d);
            case ParameterType.Bool:
                return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? null : $"«{value}» — ожидается true или false";
            case ParameterType.Date:
            case ParameterType.DateTime:
                return TryEvaluateDate(value, DateTime.Now, out _)
                    ? null : $"«{value}» — не дата (yyyy-MM-dd [HH:mm[:ss]], now, today, today-7, now-1h)";
            case ParameterType.Guid:
                return Guid.TryParse(value, out _) ? null : $"«{value}» — не GUID";
            case ParameterType.List:
                return parameter.Editable || parameter.Items.Any(i => i.Key == value)
                    ? null : $"«{value}» нет в списке items";
            case ParameterType.Range:
                if (TryParseRange(value, out var numbers) is { } rangeError)
                    return rangeError;
                return numbers.Select(n => CheckRange(parameter, n)).FirstOrDefault(e => e is not null);
            default:
                return null;
        }
    }

    /// <summary>
    /// Дата из литерала или выражения: <c>now</c>, <c>today</c>, со сдвигом <c>today-7</c>, <c>now-1h</c>, <c>now+30m</c>
    /// (единицы d, h, m; по умолчанию d).
    /// </summary>
    public static bool TryEvaluateDate(string text, DateTime now, out DateTime value)
    {
        var m = DateExpressionRegex().Match(text);
        if (m.Success)
        {
            value = m.Groups["base"].Value.Equals("today", StringComparison.OrdinalIgnoreCase) ? now.Date : now;
            if (m.Groups["sign"].Success)
            {
                var amount = int.Parse(m.Groups["amount"].Value, CultureInfo.InvariantCulture)
                    * (m.Groups["sign"].Value == "-" ? -1 : 1);
                value = m.Groups["unit"].Value.ToLowerInvariant() switch
                {
                    "h" => value.AddHours(amount),
                    "m" => value.AddMinutes(amount),
                    _ => value.AddDays(amount),
                };
            }
            return true;
        }

        return DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>Сколько номеров может дать range — защита от опечатки вроде 1..1000000.</summary>
    public const int MaxRangeCount = 10_000;

    /// <summary>
    /// Числа и диапазоны <c>a..b</c> через запятую, «;» или с новой строки, в порядке записи;
    /// <c>b &lt; a</c> — по убыванию. Возвращает текст ошибки или null.
    /// </summary>
    public static string? TryParseRange(string text, out List<long> numbers)
    {
        numbers = [];
        foreach (var part in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = RangeItemRegex().Match(part);
            if (!m.Success
                || !long.TryParse(m.Groups["from"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var from))
                return $"«{part}» — не число и не диапазон (1..5)";
            var to = from;
            if (m.Groups["to"].Success
                && !long.TryParse(m.Groups["to"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out to))
                return $"«{part}» — не число и не диапазон (1..5)";
            if (numbers.Count + Math.Abs((decimal)to - from) + 1 > MaxRangeCount)
                return $"больше {MaxRangeCount} номеров";

            var step = to >= from ? 1 : -1;
            for (var n = from; ; n += step)
            {
                numbers.Add(n);
                if (n == to)
                    break;
            }
        }

        return numbers.Count == 0 ? "нет ни одного номера" : null;
    }

    /// <summary>Число с точкой; без разделителя тысяч, иначе «4,2» читается как 42.</summary>
    public static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float & ~NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);

    private static string? CheckRange(ScriptParameter parameter, decimal value)
    {
        if (parameter.Min is { } min && value < min) return $"значение меньше минимального ({min})";
        if (parameter.Max is { } max && value > max) return $"значение больше максимального ({max})";
        return null;
    }

    [GeneratedRegex(@"^\s*(?<base>now|today)\s*(?:(?<sign>[+-])\s*(?<amount>\d+)\s*(?<unit>[dhm])?)?\s*$", RegexOptions.IgnoreCase)]
    internal static partial Regex DateExpressionRegex();

    [GeneratedRegex(@"^(?<from>-?\d+)(?:\s*\.\.\s*(?<to>-?\d+))?$")]
    private static partial Regex RangeItemRegex();
}
