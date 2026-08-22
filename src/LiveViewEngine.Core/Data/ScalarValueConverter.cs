using System.Globalization;

namespace LiveViewEngine.Core.Data;

public static class ScalarValueConverter
{
    public const string TrueString = "true";
    public const string FalseString = "false";

    public static bool TryConvertBoolean(string? raw, out bool value)
    {
        if (raw is null)
        {
            value = default;
            return false;
        }

        if (raw.Length == 1)
        {
            if (raw[0] == '1')
            {
                value = true;
                return true;
            }

            if (raw[0] == '0')
            {
                value = false;
                return true;
            }
        }

        if (bool.TryParse(raw, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string FormatBoolean(bool value) => value ? TrueString : FalseString;

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