using System.Text;
using Cleanse.Api.Imports;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Api.Test;

/// <summary>
/// The file upload's reader and the dataset's reader, given the same SharePoint items, must lead
/// to the same issue updates: the same dates as the same moments, so history ids match and no
/// date received changes when processing takes over from uploads. The CSV is as SharePoint's
/// export wrote it, with UK dates; the dataset as the connector writes it, with offsets.
/// </summary>
public sealed class SpolDatasetEquivalenceTests
{
    private const string Header =
        "Date Received,Priority,CPH,Rule No,Rule Name,Error Code,Error Code Description," +
        "Status,Assigned to,1st Contact Date,2nd Contact Date,3rd Contact Date," +
        "Letter/email sent,Notes,Date Complete";

    private static readonly string[] CsvRows =
    [
        "12/12/2025,3. Medium,01/036/8000,1,Rule Name,1,Desc,Completed,Kyle.Lloyd2@apha.gov.uk," +
            "11/05/2026,,,01/04/2026 15:16,\"archived on SAM. no longer on CTS NFA\",",
        "29/03/2026,4. Low,06/206/0015,9,Rule Name,1,Desc,Unable to Action,," +
            ",,,,\"Landline disconnected\",30/03/2026",
        "30/03/2026,4. Low,06/206/0015,9,Rule Name,1,Desc,Completed,StJohn.Hawkins1@defra.gov.uk," +
            ",25/10/2026,26/10/2026,,\"Mobile already on SAM. NFA\",25/10/2026",
        "01/12/2025,4. Low,,2,Rule Name,1,Desc,New,,,,,,\"\",",
        "14/10/2016,1. High,01/036/8000,2,Rule Name,1,Desc,In progress,stjohn.hawkins1@defra.gov.uk," +
            ",,,,\"\",",
        "10/12/2025,,01/045/0197,6,Rule Name,1,Desc,New,," +
            ",,,,\"1st attempt - no answer\n\n2nd attempt - spoke to keeper\",01/12/2025"
    ];

    private static readonly SpolDatasetRowDto[] DatasetRows =
    [
        new()
        {
            ItemId = 1, DateReceived = "2025-12-12T00:00:00+00:00", Priority = "3. Medium", Cph = "01/036/8000",
            RuleNo = "1", Status = "Completed", AssignedName = "Kyle Lloyd", AssignedEmail = "Kyle.Lloyd2@apha.gov.uk",
            FirstContactDate = "2026-05-11T00:00:00+01:00", LetterEmailSent = "2026-04-01T15:16:00+01:00",
            Notes = "archived on SAM. no longer on CTS NFA"
        },
        new()
        {
            ItemId = 2, DateReceived = "2026-03-29T00:00:00+00:00", Priority = "4. Low", Cph = "06/206/0015",
            RuleNo = "9", Status = "Unable to Action", Notes = "Landline disconnected",
            DateComplete = "2026-03-30T00:00:00+01:00"
        },
        new()
        {
            ItemId = 3, DateReceived = "2026-03-30T00:00:00+01:00", Priority = "4. Low", Cph = "06/206/0015",
            RuleNo = "9", Status = "Completed", AssignedName = "StJohn Hawkins",
            AssignedEmail = "StJohn.Hawkins1@defra.gov.uk", SecondContactDate = "2026-10-25T00:00:00+01:00",
            ThirdContactDate = "2026-10-26T00:00:00+00:00", Notes = "Mobile already on SAM. NFA",
            DateComplete = "2026-10-25T00:00:00+01:00"
        },
        new() { ItemId = 4, DateReceived = "2025-12-01T00:00:00+00:00", Priority = "4. Low", RuleNo = "2", Status = "New" },
        new()
        {
            ItemId = 5, DateReceived = "2016-10-14T00:00:00+01:00", Priority = "1. High", Cph = "01/036/8000",
            RuleNo = "2", Status = "In progress", AssignedName = "StJohn Hawkins",
            AssignedEmail = "stjohn.hawkins1@defra.gov.uk"
        },
        new()
        {
            ItemId = 6, DateReceived = "2025-12-10T00:00:00+00:00", Cph = "01/045/0197", RuleNo = "6", Status = "New",
            Notes = "1st attempt - no answer\n\n2nd attempt - spoke to keeper", DateComplete = "2025-12-01T00:00:00+00:00"
        }
    ];

    [Fact]
    public void The_same_items_read_from_a_file_or_a_dataset_make_the_same_updates()
    {
        var fromFile = SpolRowMapper.Project(SpolCsvReader.Read(
            new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", [Header, .. CsvRows]))), "cts-sam"));
        var fromDataset = SpolRowMapper.Project(SpolDatasetReader.Read(
            DatasetRows.Select(row => new SpolDatasetItem(row.ItemId, SpolDatasetRows.Serialize(row))), "cts-sam"));

        Assert.Equal(Fingerprint(fromFile), Fingerprint(fromDataset));
        Assert.Equal(1, fromDataset.DuplicatesCollapsed);
        Assert.Single(fromDataset.Invalid);
        Assert.Equal(2, fromDataset.SuspectDates.Count);
        Assert.Single(fromDataset.AssigneeConflicts);
    }

    private static string Fingerprint(SpolBatch batch) =>
        string.Join("\n",
        [
            .. batch.Rows
                .OrderBy(r => r.IssueId.Value, StringComparer.Ordinal)
                .Select(r => string.Join('|',
                    r.IssueId.Value,
                    r.WorkflowStatus,
                    r.ExternalNotes,
                    r.Priority,
                    r.ReceivedAtUtc?.ToUniversalTime().ToString("O"),
                    string.Join(',', r.Contacts.Select(c => $"{c.Column}@{c.AtUtc.ToUniversalTime():O}:{c.ActionType}")),
                    r.HasAssignee,
                    (r.AssignedEmail ?? r.AssignedTo)?.Trim().ToLowerInvariant())),
            $"collapsed {batch.DuplicatesCollapsed}, invalid {batch.Invalid.Count}",
            .. batch.SuspectDates.Select(s => $"suspect {s.Row.ToIssueId().Value}: {s.Detail}"),
            .. batch.AssigneeConflicts.Select(c => $"conflict {c.Row.Cph} across {c.NamingRows.Count}")
        ]);
}
