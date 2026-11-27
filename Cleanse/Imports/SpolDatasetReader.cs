using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Api.Imports;

/// <summary>
/// Reads a dataset's rows as the import applies them. Everything is taken literally from the
/// columns: no meaning is inferred from the free-text notes, so the import never asserts more than
/// the source says. Dates carry their offsets, so they are the same moments the file upload gave
/// them, and the history they become has the same ids.
/// </summary>
public static class SpolDatasetReader
{
    /// <summary>Who the import's changes are recorded against, and its history's source.</summary>
    public const string Source = "spol";

    // The history's column names, as the file upload's headers gave them: they are part of its ids.
    private static readonly (string Column, HistoryActionType Action, Func<SpolDatasetRowDto, string?> Value)[] ContactColumns =
    [
        ("1st Contact Date", HistoryActionType.PhoneCall, row => row.FirstContactDate),
        ("2nd Contact Date", HistoryActionType.PhoneCall, row => row.SecondContactDate),
        ("3rd Contact Date", HistoryActionType.PhoneCall, row => row.ThirdContactDate),
        ("Letter/email sent", HistoryActionType.LetterSent, row => row.LetterEmailSent)
    ];

    public static IEnumerable<SpolRow> Read(IEnumerable<SpolDatasetItem> items, string groupId)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        foreach (var item in items)
        {
            yield return ToRow(item.ItemId, SpolDatasetRows.Deserialize(item.Data), groupId);
        }
    }

    public static SpolRow ToRow(long itemId, SpolDatasetRowDto row, string groupId)
    {
        ArgumentNullException.ThrowIfNull(row);

        var contacts = new List<ExternalContactEvent>();
        foreach (var (column, action, value) in ContactColumns)
        {
            if (SpolDatasetRows.TryParseDate(value(row), out var at))
            {
                contacts.Add(new ExternalContactEvent(column, action, at));
            }
        }

        var people = People(row.AssignedName, row.AssignedEmail);
        return new SpolRow
        {
            RowNumber = itemId,
            GroupId = groupId,
            Cph = Text(row.Cph),
            RuleNo = Text(row.RuleNo),
            Status = Text(row.Status),
            Notes = Text(row.Notes),
            HasAssignee = true,
            AssignedTo = people.Count > 0 ? people[0].Name : null,
            AssignedEmail = people.Count > 0 ? people[0].Email : null,
            Assignees = people,
            Priority = Text(row.Priority),
            DateReceived = SpolDatasetRows.TryParseDate(row.DateReceived, out var received) ? received : null,
            DateComplete = SpolDatasetRows.TryParseDate(row.DateComplete, out var complete) ? complete : null,
            Contacts = contacts
        };
    }

    /// <summary>
    /// SharePoint gives a person column's names and email addresses separately, each separated by
    /// semicolons when there are several people, and in the same order.
    /// </summary>
    private static List<ExternalAssignee> People(string? names, string? emails)
    {
        var named = Split(names);
        var addressed = Split(emails);
        var people = new List<ExternalAssignee>();
        for (var index = 0; index < Math.Max(named.Length, addressed.Length); index++)
        {
            var name = index < named.Length ? Text(named[index]) : null;
            var email = index < addressed.Length ? Text(addressed[index]) : null;
            if (name is not null || email is not null)
            {
                people.Add(new ExternalAssignee(email, name));
            }
        }

        return people;
    }

    private static string[] Split(string? value) => value?.Split(';') ?? [];

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
