using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Core.Contracts;

/// <summary>
/// The rules a dataset row must keep, and the JSON it is stored as. A page is checked whole, so
/// one bad row refuses it before anything is stored.
/// </summary>
public static class SpolDatasetRows
{
    /// <summary>A SharePoint multiple-lines-of-text column holds up to 63,999 characters.</summary>
    public const int MaxValueLength = 64_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The columns SharePoint holds as dates and times, by their JSON names.</summary>
    private static readonly HashSet<string> DateProperties = new(StringComparer.Ordinal)
    {
        "dateReceived", "firstContactDate", "secondContactDate", "thirdContactDate", "letterEmailSent",
        "dateComplete", "modified", "created", "retentionLabelApplied"
    };

    private static readonly (string Name, PropertyInfo Property)[] TextProperties =
    [
        .. typeof(SpolDatasetRowDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (JsonNamingPolicy.CamelCase.ConvertName(property.Name), property))
    ];

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"];

    private static readonly HashSet<string> PropertyNames = new(
        typeof(SpolDatasetRowDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a row may have a property of this name. Names are matched ignoring case.</summary>
    public static bool IsProperty(string name) => PropertyNames.Contains(name);

    /// <summary>Null when every row can be stored; otherwise what is wrong with the first that can't.</summary>
    public static DomainError? Validate(IReadOnlyList<SpolDatasetRowDto?>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return DomainErrors.Validation("A page must have at least one row.");
        }

        if (rows.Count > SpolDataset.MaxRowsPerPage)
        {
            return DomainErrors.DatasetTooLarge(
                $"A page can have up to {SpolDataset.MaxRowsPerPage} rows, not {rows.Count}.");
        }

        var seen = new HashSet<int>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row is null || row.ItemId < 1)
            {
                return DomainErrors.Validation(
                    $"Row {index + 1} of the page has no itemId. Every row needs its SharePoint item ID.");
            }

            var item = row.ItemId.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(row.ItemId))
            {
                return DomainErrors.Validation($"Item {item} is in the page twice.", item);
            }

            foreach (var (name, property) in TextProperties)
            {
                var value = (string?)property.GetValue(row);
                if (value is null)
                {
                    continue;
                }

                if (value.Length > MaxValueLength)
                {
                    return DomainErrors.Validation(
                        $"Item {item}: {name} has {value.Length} characters. The limit is {MaxValueLength}.", item);
                }

                if (DateProperties.Contains(name) && !string.IsNullOrWhiteSpace(value) && !TryParseDate(value, out _))
                {
                    return DomainErrors.Validation(
                        $"Item {item}: {name} is not a date and time with an offset, such as 2026-04-01T00:00:00+01:00.",
                        item);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An ISO 8601 date and time with an offset or Z, as UTC. A value without one is refused: on
    /// its own, 2026-04-01T00:00:00 could be either of two moments an hour apart.
    /// </summary>
    public static bool TryParseDate(string? value, out DateTimeOffset result)
    {
        result = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !HasOffset(text) ||
            !DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        result = parsed.ToUniversalTime();
        return true;
    }

    public static string Serialize(SpolDatasetRowDto row) => JsonSerializer.Serialize(row, Json);

    public static SpolDatasetRowDto Deserialize(string data) =>
        JsonSerializer.Deserialize<SpolDatasetRowDto>(data, Json)
        ?? throw new JsonException("A stored dataset row is null.");

    /// <summary>A row as a request carries it. Throws <see cref="JsonException"/> when it is not one.</summary>
    public static SpolDatasetRowDto Deserialize(JsonElement row) =>
        row.Deserialize<SpolDatasetRowDto>(Json) ?? throw new JsonException("A row is null.");

    private static bool HasOffset(string text) =>
        text[^1] is 'Z' or 'z' ||
        (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
}
