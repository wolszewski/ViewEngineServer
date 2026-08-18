using System.Globalization;

namespace LiveViewEngine.Core.Data;

public static class ScalarValueConverter
{
    public static bool TryConvertInt32(string? raw, out int value)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryConvertInt64(string? raw, out long value)
    {
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryConvertDouble(string? raw, out double value)
    {
        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryConvertDecimal(string? raw, out decimal value)
    {
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryConvertDateOnly(string? raw, out DateOnly value)
    {
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    public static bool TryConvertDateTime(string? raw, out DateTime value)
    {
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out value);
    }

    public static bool TryConvertDateTimeOffset(string? raw, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out value);
    }

}