using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Tests;

public sealed class ParameterValuesTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 14, 30, 15);

    [Theory]
    [InlineData("now", "2026-11-05 14:30:15")]
    [InlineData("today", "2026-11-05 00:00:00")]
    [InlineData("today-7", "2026-10-29 00:00:00")]
    [InlineData("Today + 1d", "2026-11-06 00:00:00")]
    [InlineData("now-1h", "2026-11-05 13:30:15")]
    [InlineData("now+30m", "2026-11-05 15:00:15")]
    [InlineData("2026-01-02", "2026-01-02 00:00:00")]
    [InlineData("2026-01-02T03:04", "2026-01-02 03:04:00")]
    [InlineData("02.01.2026 03:04:05", "2026-01-02 03:04:05")]
    public void DateExpressions(string text, string expected)
    {
        Assert.True(ParameterValues.TryEvaluateDate(text, Now, out var value));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("now-")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    public void InvalidDates(string text) =>
        Assert.False(ParameterValues.TryEvaluateDate(text, Now, out _));

    [Theory]
    [InlineData("1, 5, 2..4, 6..8", "1 5 2 3 4 6 7 8")]
    [InlineData("19..21\n1..2\r\n 7 ", "19 20 21 1 2 7")]
    [InlineData("3..1; 5", "3 2 1 5")]
    [InlineData("4,4", "4 4")]
    public void Ranges(string text, string expected)
    {
        Assert.Null(ParameterValues.TryParseRange(text, out var numbers));
        Assert.Equal(expected, string.Join(" ", numbers));

        var resolved = ParameterValues.Resolve(new ScriptParameter("servers", ParameterType.Range), [text], Now);
        Assert.Equal(numbers, (long[])resolved!);
    }

    [Theory]
    [InlineData("1..")]
    [InlineData("a")]
    [InlineData("1-5")]
    [InlineData(", ,")]
    [InlineData("1..20000")]
    public void InvalidRanges(string text) =>
        Assert.NotNull(ParameterValues.TryParseRange(text, out _));

    [Fact]
    public void RangeLimits()
    {
        var p = new ScriptParameter("servers", ParameterType.Range) { Min = 1, Max = 43 };
        Assert.Null(ParameterValues.Validate(p, "1..43"));
        Assert.NotNull(ParameterValues.Validate(p, "40..44"));
    }

    [Theory]
    [InlineData(ParameterType.Int, "42", true)]
    [InlineData(ParameterType.Int, "4.2", false)]
    [InlineData(ParameterType.Decimal, "4.2", true)]
    [InlineData(ParameterType.Decimal, "4,2", false)]
    [InlineData(ParameterType.Bool, "TRUE", true)]
    [InlineData(ParameterType.Bool, "1", false)]
    [InlineData(ParameterType.Guid, "6f9619ff-8b86-d011-b42d-00cf4fc964ff", true)]
    [InlineData(ParameterType.Guid, "-", false)]
    [InlineData(ParameterType.String, "-", true)]
    public void Validate(ParameterType type, string value, bool valid) =>
        Assert.Equal(valid, ParameterValues.Validate(new ScriptParameter("p", type), value) is null);

    [Fact]
    public void Range()
    {
        var p = new ScriptParameter("p", ParameterType.Int) { Min = 0, Max = 10 };

        Assert.Null(ParameterValues.Validate(p, "10"));
        Assert.NotNull(ParameterValues.Validate(p, "-1"));
        Assert.NotNull(ParameterValues.Validate(p, "11"));
    }

    [Fact]
    public void ValidateField()
    {
        var required = new ScriptParameter("p", ParameterType.Int);
        var optional = required with { Required = false };
        var multi = new ScriptParameter("m", ParameterType.List) { Multi = true, Items = [new("a", "a"), new("b", "b")] };

        Assert.Equal("Обязательный параметр", ParameterValues.ValidateField(required, ["  "]));
        Assert.Null(ParameterValues.ValidateField(optional, []));
        Assert.Null(ParameterValues.ValidateField(optional, [" 5 "]));
        Assert.NotNull(ParameterValues.ValidateField(optional, ["1", "2"]));
        Assert.Null(ParameterValues.ValidateField(multi, ["a", "b"]));
        Assert.NotNull(ParameterValues.ValidateField(multi, ["a", "c"]));
    }

    [Fact]
    public void Resolve()
    {
        object? R(ScriptParameter p, params string[] v) => ParameterValues.Resolve(p, v, Now);

        Assert.Equal(42L, R(new("p", ParameterType.Int), " 42 "));
        Assert.Equal(0.5m, R(new("p", ParameterType.Decimal), "0.5"));
        Assert.Equal(true, R(new("p", ParameterType.Bool), "True"));
        Assert.Equal(new DateTime(2026, 10, 29), R(new("p", ParameterType.Date), "now-7"));
        Assert.Equal(new DateTime(2026, 11, 5, 13, 30, 15), R(new("p", ParameterType.DateTime), "now-1h"));
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), R(new("p", ParameterType.Guid), "6f9619ff-8b86-d011-b42d-00cf4fc964ff"));
        Assert.Equal(" как есть\n", R(new("p", ParameterType.Text), " как есть\n"));
        Assert.Equal("x", R(new("p", ParameterType.String), " x "));
        Assert.Null(R(new("p", ParameterType.String) { Required = false }, ""));
        Assert.Equal(new[] { "a" }, R(new("p", ParameterType.List) { Multi = true, Required = false, Items = [new("a", "A")] }, "a", ""));
        Assert.Throws<ArgumentException>(() => R(new("p", ParameterType.Int), "x"));
    }

    [Theory]
    [InlineData("2026-01-02 00:00:00", false, "2026-01-02")]
    [InlineData("2026-01-02 03:04:00", true, "2026-01-02 03:04")]
    [InlineData("2026-01-02 03:04:05", true, "2026-01-02 03:04:05")]
    public void FormatDate(string value, bool withTime, string expected) =>
        Assert.Equal(expected, ParameterValues.FormatDate(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), withTime));

    [Fact]
    public void ListUsesKeys()
    {
        var p = new ScriptParameter("p", ParameterType.List) { Items = [new("1", "Тест")] };

        Assert.Null(ParameterValues.Validate(p, "1"));
        Assert.NotNull(ParameterValues.Validate(p, "Тест"));
        Assert.Null(ParameterValues.Validate(p with { Editable = true }, "своё"));
    }
}
