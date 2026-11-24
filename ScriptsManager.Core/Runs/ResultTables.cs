using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace ScriptsManager.Core.Runs;

public static class ResultTables
{
    /// <summary>Сколько байт бинарного значения показывать в таблице.</summary>
    public const int MaxBinaryBytes = 1024;

    /// <summary>Читает текущий результирующий набор в таблицу.</summary>
    public static DataTable Read(DbDataReader reader, CancellationToken ct)
    {
        var table = new DataTable();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var column = table.Columns.Add("c" + i.ToString(CultureInfo.InvariantCulture), typeof(object));
            column.Caption = name.Length > 0 ? name : "(без имени)";
        }

        var values = new object[reader.FieldCount];
        table.BeginLoadData();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            reader.GetValues(values);
            for (var i = 0; i < values.Length; i++)
                values[i] = Display(values[i]);
            table.Rows.Add(values);
        }

        table.EndLoadData();
        return table;
    }

    /// <summary>Бинарные значения — как в SSMS (0x…), с ограничением длины.</summary>
    public static object Display(object value) => value switch
    {
        byte[] bytes => Hex(bytes),
        _ => value,
    };

    public static string Hex(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, MaxBinaryBytes);
        var sb = new StringBuilder(2 + shown * 2 + 32).Append("0x").Append(Convert.ToHexString(bytes, 0, shown));
        if (bytes.Length > shown)
            sb.Append(CultureInfo.InvariantCulture, $"… ({bytes.Length} байт)");
        return sb.ToString();
    }

    /// <summary>CSV для Excel: разделитель «;», значения в кавычках при необходимости, даты в ISO.</summary>
    public static void WriteCsv(DataTable table, TextWriter writer)
    {
        writer.WriteLine(string.Join(";", table.Columns.Cast<DataColumn>().Select(c => Quote(c.Caption))));
        foreach (DataRow row in table.Rows)
            writer.WriteLine(string.Join(";", row.ItemArray.Select(v => Quote(Format(v)))));
    }

    public static string Format(object? value) => value switch
    {
        null or DBNull => "",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Quote(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
