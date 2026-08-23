using System.Globalization;

namespace LiveViewEngine.Core.Data;

public static class ScalarValueConverter
{
    private const string TrueString = "true";
    private const string FalseString = "false";

    public static bool? ParseBoolean(string? raw)
    {
        if (string.Equals(raw, TrueString, StringComparison.Ordinal)) { return true; }
        if (string.Equals(raw, FalseString, StringComparison.Ordinal)) { return false; }
        return null;
    }

    public static int? ParseInt32(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static long? ParseInt64(string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static double? ParseDouble(string? raw) =>
        double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static decimal? ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static DateOnly? ParseDateOnly(string? raw) =>
        DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var v) ? v : null;

    public static DateTime? ParseDateTime(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var v) ? v : null;

    public static DateTimeOffset? ParseDateTimeOffset(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var v) ? v : null;

    public static string FormatBoolean(bool value) => value ? TrueString : FalseString;
}
