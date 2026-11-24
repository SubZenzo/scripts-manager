using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScriptsManager.Core.Runs;

/// <summary>Значения параметров в текстовом виде — для set_config, аргументов процессов и SCRIPT_PARAMS.</summary>
public static class RunValues
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Строка значения: даты в ISO, числа с точкой, bool — true/false, список — JSON-массив; null — null.</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        string[] list => Json(list),
        long[] numbers => Json(numbers),
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static string Json(object? value) => JsonSerializer.Serialize(value switch
    {
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(p => p.Key, p => p.Value is string[] or long[] ? p.Value : (object?)Text(p.Value)),
        _ => value,
    }, JsonOptions);
}
