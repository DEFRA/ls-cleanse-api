using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Maintenance;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Commands.Settings;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Queries.Settings;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public class SettingsTests
{
    private static readonly string Subject = AuditSubjects.Setting(ServiceSettings.MinimumIssueAgeDaysName);

    private static async Task<IReadOnlyList<AuditEntry>> AuditAsync(CleanseHarness harness) =>
        (await harness.Audit.GetForSubjectAsync(Subject, PageRequest.Default)).Items;

    [Fact]
    public async Task The_minimum_issue_age_is_fourteen_days_until_an_admin_changes_it()
    {
        using var harness = new CleanseHarness();

        Assert.Equal(14, (await harness.QueryAsync(new GetSettingsQuery())).MinimumIssueAgeDays);
    }

    [Fact]
    public async Task A_change_is_saved_and_audited_against_the_admin()
    {
        using var harness = new CleanseHarness();

        var updated = await harness.SendAsync(new UpdateSettingsCommand(7, TestData.Alice));

        Assert.Equal(7, updated.MinimumIssueAgeDays);
        Assert.Equal(7, (await harness.QueryAsync(new GetSettingsQuery())).MinimumIssueAgeDays);
        var entry = Assert.Single(await AuditAsync(harness));
        Assert.Equal(AuditEventKind.SettingChanged, entry.Kind);
        Assert.Equal(TestData.Alice, entry.Actor);
        Assert.Equal(TestData.Night1, entry.AtUtc);
        Assert.Equal(new AuditChange(nameof(ServiceSettings.MinimumIssueAgeDays), "14", "7"), Assert.Single(entry.Changes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task An_age_out_of_range_is_refused_and_nothing_changes(int days)
    {
        using var harness = new CleanseHarness();

        var result = await harness.TrySendAsync(new UpdateSettingsCommand(days, TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error.Code);
        Assert.Equal(14, (await harness.QueryAsync(new GetSettingsQuery())).MinimumIssueAgeDays);
        Assert.Empty(await AuditAsync(harness));
    }

    [Fact]
    public async Task Saving_the_age_it_already_has_records_nothing()
    {
        using var harness = new CleanseHarness();

        await harness.SendAsync(new UpdateSettingsCommand(14, TestData.Alice));
        await harness.SendAsync(new UpdateSettingsCommand(7, TestData.Alice));
        await harness.SendAsync(new UpdateSettingsCommand(7, TestData.Alice));

        Assert.Single(await AuditAsync(harness));
    }

    [Fact]
    public async Task A_reset_keeps_the_settings_and_their_audit()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new UpdateSettingsCommand(7, TestData.Alice));

        await harness.SendAsync(new ResetDataCommand(null, TestData.Alice));

        Assert.Equal(7, (await harness.QueryAsync(new GetSettingsQuery())).MinimumIssueAgeDays);
        Assert.Single(await AuditAsync(harness));
    }

    [Fact]
    public async Task Searches_that_ask_show_an_issue_only_once_it_is_old_enough()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("01/001/0001"), TestData.Detection("01/001/0002")], Actor.System));
        var filter = new IssueFilter { GroupId = TestData.Group };

        harness.Clock.AdvanceDays(13);

        Assert.Equal(0, (await harness.QueryAsync(new SearchIssuesQuery(filter, ApplyMinimumAge: true))).TotalCount);
        Assert.Equal(0, (await harness.QueryAsync(new SearchIssuesByCphQuery(filter, ApplyMinimumAge: true))).TotalCount);
        Assert.Equal(2, (await harness.QueryAsync(new SearchIssuesQuery(filter))).TotalCount);
        Assert.Equal(2, (await harness.QueryAsync(new SearchIssuesByCphQuery(filter))).TotalCount);

        harness.Clock.AdvanceDays(1);

        Assert.Equal(2, (await harness.QueryAsync(new SearchIssuesQuery(filter, ApplyMinimumAge: true))).TotalCount);
        Assert.Equal(2, (await harness.QueryAsync(new SearchIssuesByCphQuery(filter, ApplyMinimumAge: true))).TotalCount);
    }

    [Fact]
    public async Task An_admins_change_applies_to_the_next_search()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("01/001/0001")], Actor.System));
        var filter = new IssueFilter { GroupId = TestData.Group };
        harness.Clock.AdvanceDays(8);

        Assert.Equal(0, (await harness.QueryAsync(new SearchIssuesQuery(filter, ApplyMinimumAge: true))).TotalCount);

        await harness.SendAsync(new UpdateSettingsCommand(7, TestData.Alice));
        Assert.Equal(1, (await harness.QueryAsync(new SearchIssuesQuery(filter, ApplyMinimumAge: true))).TotalCount);

        await harness.SendAsync(new UpdateSettingsCommand(0, TestData.Alice));
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("01/001/0002")], Actor.System));
        Assert.Equal(2, (await harness.QueryAsync(new SearchIssuesQuery(filter, ApplyMinimumAge: true))).TotalCount);
    }
}
