using System.Globalization;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using CsvHelper;
using CsvHelper.Configuration;

namespace Cleanse.Api.Imports;

/// <summary>
/// Reads the SharePoint list export. Everything is taken literally from the columns: no meaning
/// is inferred from the free-text notes, so the import never asserts more than the source says.
/// </summary>
public sealed class SpolCsvReader
{
    public const string Source = "spol";

    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,
        BadDataFound = null
    };

    /// <summary>Columns the extract must carry, checked before a byte is persisted.</summary>
    public static readonly string[] RequiredColumns =
        ["CPH", "Rule No", "Status", "Notes", "Priority", "Date Received"];

    private static readonly (string Column, HistoryActionType Action)[] ContactColumns =
    [
        ("1st Contact Date", HistoryActionType.PhoneCall),
        ("2nd Contact Date", HistoryActionType.PhoneCall),
        ("3rd Contact Date", HistoryActionType.PhoneCall),
        ("Letter/email sent", HistoryActionType.LetterSent)
    ];

    /// <summary>What "Export CSV with schema" puts on the line before the header.</summary>
    private const string SchemaLinePrefix = "ListSchema=";

    private static readonly string[] UkDateFormats =
        ["d/M/yyyy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss"];

    /// <summary>The list's regional time zone, which SharePoint shows its dates in.</summary>
    internal static readonly TimeZoneInfo UkTime =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var london)
            ? london
            : TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    internal static string UkDay(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, UkTime).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static bool TryValidateHeader(Stream content, out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = OpenText(content, leaveOpen: true, out _);
        using var csv = new CsvReader(reader, Configuration);

        if (!csv.Read() || !csv.ReadHeader())
        {
            error = "The file has no header row.";
            return false;
        }

        var header = csv.HeaderRecord ?? [];
        var missing = RequiredColumns
            .Where(c => !header.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (missing.Length > 0)
        {
            error = $"The file is missing required columns: {string.Join(", ", missing)}.";
            return false;
        }

        error = null;
        return true;
    }

    public static IEnumerable<SpolRow> Read(Stream content, string groupId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        using var reader = OpenText(content, leaveOpen: false, out var skippedLines);
        using var csv = new CsvReader(reader, Configuration);

        csv.Read();
        csv.ReadHeader();

        // Numbered as a spreadsheet shows the file, so the schema line counts as row 1.
        long rowNumber = 1 + skippedLines;
        while (csv.Read())
        {
            rowNumber++;
            yield return ReadRow(csv, groupId, rowNumber);
        }
    }

    /// <summary>
    /// Drops the schema line as text rather than as a record: it is JSON full of quotes, which a
    /// CSV parser could read straight past the end of the line, swallowing the header.
    /// </summary>
    private static TextReader OpenText(Stream content, bool leaveOpen, out int skippedLines)
    {
        var reader = new StreamReader(content, leaveOpen: leaveOpen);
        var first = reader.ReadLine();
        if (first is not null && first.StartsWith(SchemaLinePrefix, StringComparison.Ordinal))
        {
            skippedLines = 1;
            return reader;
        }

        skippedLines = 0;
        return first is null ? reader : new ReplayingReader(first + "\n", reader);
    }

    private static SpolRow ReadRow(IReaderRow csv, string groupId, long rowNumber)
    {
        var cph = Field(csv, "CPH");
        var ruleNo = Field(csv, "Rule No");

        var contacts = new List<ExternalContactEvent>();
        foreach (var (column, action) in ContactColumns)
        {
            if (TryDate(Field(csv, column), out var at))
            {
                contacts.Add(new ExternalContactEvent(column, action, at));
            }
        }

        return new SpolRow
        {
            RowNumber = rowNumber,
            GroupId = groupId,
            Cph = cph,
            RuleNo = ruleNo,
            Status = Field(csv, "Status"),
            Notes = Field(csv, "Notes"),
            HasAssignee = csv.HeaderRecord?.Contains("Assigned to", StringComparer.OrdinalIgnoreCase) == true,
            AssignedTo = Field(csv, "Assigned to"),
            Priority = Field(csv, "Priority"),
            DateReceived = TryDate(Field(csv, "Date Received"), out var received) ? received : null,
            DateComplete = TryDate(Field(csv, "Date Complete"), out var complete) ? complete : null,
            Contacts = contacts
        };
    }

    private static string? Field(IReaderRow csv, string column) =>
        csv.TryGetField<string>(
            csv.HeaderRecord?.FirstOrDefault(header => string.Equals(header, column, StringComparison.OrdinalIgnoreCase)) ?? column,
            out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // A UK date is a day in UK time: the instant the ISO export carries for the same cell, so
        // both exports produce the same timestamps and therefore the same history ids.
        if (DateTime.TryParseExact(value, UkDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            result = new DateTimeOffset(local, UkTime.GetUtcOffset(local)).ToUniversalTime();
            return true;
        }

        // A slashed date that is not UK-shaped must fail: the invariant parser would read it as US.
        if (value.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out result);
    }

    /// <summary>Replays a line that has already been read, then hands over to the rest.</summary>
    private sealed class ReplayingReader(string replay, TextReader rest) : TextReader
    {
        private int _position;

        public override int Peek() => _position < replay.Length ? replay[_position] : rest.Peek();

        public override int Read() => _position < replay.Length ? replay[_position++] : rest.Read();

        public override int Read(char[] buffer, int index, int count)
        {
            if (_position >= replay.Length)
            {
                return rest.Read(buffer, index, count);
            }

            var copied = Math.Min(count, replay.Length - _position);
            replay.CopyTo(_position, buffer, index, copied);
            _position += copied;
            return copied;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>One raw row, before it is turned into an issue update.</summary>
public sealed record SpolRow
{
    public required long RowNumber { get; init; }

    public required string GroupId { get; init; }

    public string? Cph { get; init; }

    public string? RuleNo { get; init; }

    public string? Status { get; init; }

    public string? Notes { get; init; }

    public bool HasAssignee { get; init; }

    public string? AssignedTo { get; init; }

    public string? Priority { get; init; }

    public DateTimeOffset? DateReceived { get; init; }

    /// <summary>Used only to break ties between rows sharing a key.</summary>
    public DateTimeOffset? DateComplete { get; init; }

    public IReadOnlyList<ExternalContactEvent> Contacts { get; init; } = [];

    public bool IsKeyed => !string.IsNullOrWhiteSpace(Cph) && !string.IsNullOrWhiteSpace(RuleNo);

    /// <summary>
    /// Must agree with the rules engine's scheme, or every imported row would reconcile against
    /// nothing. The group is a column on the issue, not part of its identity.
    /// </summary>
    public IssueId ToIssueId() => IssueId.Derive(RuleNo!.Trim(), Cph!.Trim());
}
