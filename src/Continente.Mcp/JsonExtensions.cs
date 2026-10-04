using System.Globalization;
using System.Text.Json;

namespace Continente.Mcp;

internal static class JsonExtensions
{
    public static string RequiredString(this JsonElement element, string propertyName) =>
        element.StringOrNull(propertyName)
        ?? throw new InvalidOperationException($"Continente response is missing '{propertyName}'.");

    public static string? StringOrNull(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static string? NestedStringOrNull(this JsonElement element, string parent, string child) =>
        element.TryGetProperty(parent, out var parentElement)
            ? parentElement.StringOrNull(child)
            : null;

    public static bool Boolean(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed) &&
                parsed);
    }

    public static decimal Decimal(this JsonElement element, string propertyName, decimal fallback = 0m) =>
        element.DecimalOrNull(propertyName) ?? fallback;

    public static decimal? DecimalOrNull(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }

        return null;
    }
}
