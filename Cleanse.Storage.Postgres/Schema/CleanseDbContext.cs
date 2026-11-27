using System.Text.RegularExpressions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Domain.Mi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// The single description of the physical schema. EF generates migrations from this, and the
/// non-bulk read paths query through it, so the shape the migration creates is by construction
/// the shape the code expects.
/// </summary>
public sealed class CleanseDbContext(DbContextOptions<CleanseDbContext> options) : DbContext(options)
{
    internal DbSet<IssueRow> Issues => Set<IssueRow>();

    internal DbSet<RuleRunRow> RuleRuns => Set<RuleRunRow>();

    internal DbSet<AuditEntryRow> AuditEntries => Set<AuditEntryRow>();

    internal DbSet<IssueHistoryRow> IssueHistory => Set<IssueHistoryRow>();

    internal DbSet<ImportJobRow> ImportJobs => Set<ImportJobRow>();

    internal DbSet<ImportJobRejectionRow> ImportJobRejections => Set<ImportJobRejectionRow>();

    internal DbSet<DataResetRow> DataResets => Set<DataResetRow>();
    internal DbSet<UserRow> Users => Set<UserRow>();

    internal DbSet<HoldingRow> Holdings => Set<HoldingRow>();

    internal DbSet<HoldingNoteRow> HoldingNotes => Set<HoldingNoteRow>();

    internal DbSet<HoldingActionRow> HoldingActions => Set<HoldingActionRow>();

    internal DbSet<CodedValueRow> CodedValues => Set<CodedValueRow>();

    internal DbSet<ServiceSettingsRow> ServiceSettings => Set<ServiceSettingsRow>();

    internal DbSet<SpolDatasetRow> SpolDatasets => Set<SpolDatasetRow>();

    internal DbSet<SpolDatasetItemRow> SpolDatasetItems => Set<SpolDatasetItemRow>();

    internal DbSet<MiRefreshRow> MiRefresh => Set<MiRefreshRow>();

    internal DbSet<MiIssueStateRow> MiIssueStates => Set<MiIssueStateRow>();

    internal DbSet<MiHoldingStateRow> MiHoldingStates => Set<MiHoldingStateRow>();

    internal DbSet<MiEventRow> MiEvents => Set<MiEventRow>();

    internal DbSet<MiDailyChangeRow> MiDailyChanges => Set<MiDailyChangeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // CDP creates the database in 'public' with grants preconfigured; never emit CREATE SCHEMA.
        modelBuilder.HasDefaultSchema(null);

        ConfigureIssues(modelBuilder);
        ConfigureRuleRuns(modelBuilder);
        ConfigureAuditEntries(modelBuilder);
        ConfigureIssueHistory(modelBuilder);
        ConfigureImportJobs(modelBuilder);
        ConfigureDataResets(modelBuilder);
        ConfigureUsers(modelBuilder);
        ConfigureCodedValues(modelBuilder);
        ConfigureServiceSettings(modelBuilder);
        ConfigureHoldings(modelBuilder);
        ConfigureSpolDatasets(modelBuilder);
        ConfigureMi(modelBuilder);
    }

    /// <summary>
    /// The MI dashboard's summary, rebuilt whole from the tables above by each refresh and emptied
    /// by a reset. No foreign keys: nothing points at it, and it must not hold up a delete.
    /// </summary>
    private static void ConfigureMi(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MiRefreshRow>(entity =>
        {
            entity.ToTable(Db.MiRefresh);
            entity.HasKey(e => e.GroupId);
            SnakeCaseColumns(entity);
        });

        modelBuilder.Entity<MiIssueStateRow>(entity =>
        {
            entity.ToTable(Db.MiIssueStates);
            entity.HasKey(e => new { e.GroupId, e.IssueId });
            SnakeCaseColumns(entity);
        });

        modelBuilder.Entity<MiHoldingStateRow>(entity =>
        {
            entity.ToTable(Db.MiHoldingStates);
            entity.HasKey(e => new { e.GroupId, e.Cph });
            SnakeCaseColumns(entity);

            // The longest open holdings.
            entity.HasIndex(e => new { e.GroupId, e.OldestOpenRaisedAtUtc })
                .HasDatabaseName("ix_mi_holding_states_open")
                .HasFilter($"status IN ('{HoldingStatus.Todo.ToWire()}', '{HoldingStatus.InProgress.ToWire()}')");
        });

        modelBuilder.Entity<MiEventRow>(entity =>
        {
            entity.ToTable(Db.MiEvents);
            entity.HasKey(e => new { e.GroupId, e.Sequence });
            SnakeCaseColumns(entity);

            // Every figure for a period.
            entity.HasIndex(e => new { e.GroupId, e.Day }).HasDatabaseName("ix_mi_events_day");

            // One person's work.
            entity.HasIndex(e => new { e.GroupId, e.UserId, e.Day })
                .HasDatabaseName("ix_mi_events_person")
                .HasFilter($"source = '{MiSource.Cleanse.ToWire()}'");
        });

        modelBuilder.Entity<MiDailyChangeRow>(entity =>
        {
            entity.ToTable(Db.MiDailyChanges);
            entity.HasKey(e => new { e.GroupId, e.Unit, e.Day, e.RuleId, e.Status, e.State });
            SnakeCaseColumns(entity);
        });
    }

    /// <summary>Column names as the rest of the schema spells them: GroupId is group_id.</summary>
    private static void SnakeCaseColumns<T>(EntityTypeBuilder<T> entity)
        where T : class
    {
        foreach (var property in entity.Metadata.GetProperties())
        {
            property.SetColumnName(Regex.Replace(property.Name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant());
        }
    }

    /// <summary>
    /// What the connector loads for an admin to process. A reset keeps them, as it does users.
    /// Only the key's SHA-256 is kept, and a dataset's rows go with it.
    /// </summary>
    private static void ConfigureSpolDatasets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpolDatasetRow>(entity =>
        {
            entity.ToTable(Db.SpolDatasets, table => table.HasCheckConstraint(
                "ck_spol_datasets_status", "status IN ('Empty', 'Loading', 'Ready')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(SpolDataset.MaxNameLength);
            entity.Property(e => e.KeyHash).HasColumnName("key_hash").HasMaxLength(64);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(e => e.RowCount).HasColumnName("row_count");
            entity.Property(e => e.ExpectedRows).HasColumnName("expected_rows");
            entity.Property(e => e.PageCount).HasColumnName("page_count");
            entity.Property(e => e.CurrentLoadId).HasColumnName("current_load_id");
            entity.Property(e => e.LoadStartedAtUtc).HasColumnName("load_started_at_utc");
            entity.Property(e => e.LastLoadedAtUtc).HasColumnName("last_loaded_at_utc");
            entity.Property(e => e.CreatedById).HasColumnName("created_by_id");
            entity.Property(e => e.CreatedByName).HasColumnName("created_by_name");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(e => e.Version).HasColumnName("version");

            entity.HasIndex(e => new { e.CreatedAtUtc, e.Id })
                .HasDatabaseName("ix_spol_datasets_created")
                .IsDescending(true, true);
        });

        modelBuilder.Entity<SpolDatasetItemRow>(entity =>
        {
            entity.ToTable(Db.SpolDatasetItems);

            entity.HasKey(e => new { e.DatasetId, e.ItemId });
            entity.Property(e => e.DatasetId).HasColumnName("dataset_id");
            entity.Property(e => e.ItemId).HasColumnName("item_id").ValueGeneratedNever();
            entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");

            entity.HasOne<SpolDatasetRow>()
                .WithMany()
                .HasForeignKey(e => e.DatasetId)
                .HasConstraintName("fk_spol_dataset_items_dataset")
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// One row, absent until an admin first saves. A reset keeps it, and the audit log records who
    /// changed it and when.
    /// </summary>
    private static void ConfigureServiceSettings(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ServiceSettingsRow>(entity =>
        {
            entity.ToTable(Db.ServiceSettings, table =>
            {
                table.HasCheckConstraint("ck_service_settings_one_row", "id = 1");
                table.HasCheckConstraint("ck_service_settings_minimum_issue_age_days", "minimum_issue_age_days >= 0");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.MinimumIssueAgeDays).HasColumnName("minimum_issue_age_days");
        });

    /// <summary>
    /// The admin-maintained lists. Never deleted, by a reset or otherwise, because notes, actions
    /// and issue outcomes point at their entries.
    /// </summary>
    private static void ConfigureCodedValues(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<CodedValueRow>(entity =>
        {
            entity.ToTable(Db.CodedValues, table => table.HasCheckConstraint(
                "ck_coded_values_list",
                $"list IN ('{CodedLists.ActionTypes}', '{CodedLists.ResolvedReasons}', '{CodedLists.CantResolveReasons}')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.List).HasColumnName("list").HasMaxLength(32);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(CodedValue.MaxNameLength);
            entity.Property(e => e.NameKey).HasColumnName("name_key").HasMaxLength(CodedValue.MaxNameLength);
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");

            entity.HasIndex(e => new { e.List, e.NameKey }).IsUnique().HasDatabaseName("ux_coded_values_list_name");
        });

    /// <summary>
    /// What people record against a holding. Keyed by group and CPH like the issues' context, with
    /// no foreign key to a holding row: a note does not need the holding to have an assignee.
    /// </summary>
    private static void ConfigureHoldings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HoldingRow>(entity =>
        {
            entity.ToTable(Db.Holdings);

            entity.HasKey(e => new { e.GroupId, e.Cph });
            entity.Property(e => e.GroupId).HasColumnName(Db.Holding.GroupId);
            entity.Property(e => e.Cph).HasColumnName(Db.Holding.Cph);
            entity.Property(e => e.AssignedTo).HasColumnName(Db.Holding.AssignedTo);
            entity.Property(e => e.AssignedUserId).HasColumnName(Db.Holding.AssignedUserId);
            entity.Property(e => e.AssignedUserFullName).HasColumnName(Db.Holding.AssignedUserFullName);
            entity.Property(e => e.AssignmentDetachedAtUtc).HasColumnName(Db.Holding.AssignmentDetachedAtUtc);
            entity.Property(e => e.CreatedAtUtc).HasColumnName(Db.Holding.CreatedAtUtc);
            entity.Property(e => e.UpdatedAtUtc).HasColumnName(Db.Holding.UpdatedAtUtc);
            entity.Property(e => e.Version).HasColumnName(Db.Holding.Version);

            entity.HasOne<UserRow>().WithMany().HasForeignKey(e => e.AssignedUserId)
                .HasConstraintName("fk_holdings_assigned_user")
                .OnDelete(DeleteBehavior.Restrict);

            // The assignee filter on the triage lists.
            entity.HasIndex(e => e.AssignedUserId)
                .HasDatabaseName("ix_holdings_assigned_user")
                .HasFilter($"{Db.Holding.AssignedUserId} IS NOT NULL");
        });

        modelBuilder.Entity<HoldingNoteRow>(entity =>
        {
            entity.ToTable(Db.HoldingNotes);

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.Cph).HasColumnName("cph");
            entity.Property(e => e.Text).HasColumnName("text").HasMaxLength(HoldingNote.MaxLength);
            entity.Property(e => e.Context).HasColumnName("context").HasMaxLength(HoldingNote.MaxContextLength);
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedById).HasColumnName("created_by_id");
            entity.Property(e => e.CreatedByDisplayName).HasColumnName("created_by_display_name");
            entity.Property(e => e.CreatedByKind).HasColumnName("created_by_kind");

            entity.HasIndex(e => new { e.GroupId, e.Cph, e.CreatedAtUtc, e.Sequence })
                .HasDatabaseName("ix_holding_notes_holding")
                .IsDescending(false, false, true, true);
        });

        modelBuilder.Entity<HoldingActionRow>(entity =>
        {
            entity.ToTable(Db.HoldingActions);

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.Cph).HasColumnName("cph");
            entity.Property(e => e.ActionTypeId).HasColumnName("action_type_id");
            entity.Property(e => e.ActionTypeName).HasColumnName("action_type_name");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedById).HasColumnName("created_by_id");
            entity.Property(e => e.CreatedByDisplayName).HasColumnName("created_by_display_name");
            entity.Property(e => e.CreatedByKind).HasColumnName("created_by_kind");

            entity.HasOne<CodedValueRow>().WithMany().HasForeignKey(e => e.ActionTypeId)
                .HasConstraintName("fk_holding_actions_action_type")
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.GroupId, e.Cph, e.CreatedAtUtc, e.Sequence })
                .HasDatabaseName("ix_holding_actions_holding")
                .IsDescending(false, false, true, true);

            // MI counts actions over time.
            entity.HasIndex(e => e.CreatedAtUtc).HasDatabaseName("ix_holding_actions_created");
        });
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<UserRow>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.Issuer).HasColumnName("issuer").HasMaxLength(512);
            entity.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(256);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(64);
            entity.Property(e => e.ObjectId).HasColumnName("object_id").HasMaxLength(64);
            entity.Property(e => e.ClientId).HasColumnName("client_id").HasMaxLength(64);
            entity.Property(e => e.FullName).HasColumnName("full_name").HasMaxLength(256);
            entity.Property(e => e.FirstLoggedInAtUtc).HasColumnName("first_logged_in_at_utc");
            entity.Property(e => e.LastLoggedInAtUtc).HasColumnName("last_logged_in_at_utc");
            entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(320);
            entity.HasIndex(e => new { e.Issuer, e.ClientId, e.Subject })
                .IsUnique().HasDatabaseName("ux_users_identity");
            // Stored lower-case, so a plain unique index is case-insensitive in effect.
            entity.HasIndex(e => e.Email).IsUnique().HasDatabaseName("ux_users_email");
            entity.HasIndex(e => new { e.TenantId, e.ObjectId }).HasDatabaseName("ix_users_object");
        });

    /// <summary>
    /// Deliberately outside the reset's blast radius: a table the wipe empties cannot answer
    /// "when was this last wiped".
    /// </summary>
    private static void ConfigureDataResets(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DataResetRow>(entity =>
        {
            entity.ToTable(Db.DataResets);

            entity.HasKey(e => e.Sequence);
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.AtUtc).HasColumnName("at_utc");
            entity.Property(e => e.Scope).HasColumnName("scope");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.Issues).HasColumnName("issues");

            entity.HasIndex(e => e.AtUtc).IsDescending().HasDatabaseName("ix_data_resets_at");
        });

    private static void ConfigureIssues(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable(Db.Issues, table =>
            {
                table.HasCheckConstraint(
                    "ck_issues_status",
                    $"{Db.Issue.Status} IN ('{StatusNames.Active}', '{StatusNames.Inactive}')");
                table.HasCheckConstraint(
                    "ck_issues_workflow_status",
                    $"{Db.Issue.WorkflowStatus} IN ('{StatusNames.Todo}', '{StatusNames.InProgress}', " +
                    $"'{StatusNames.Suppressed}', '{StatusNames.Resolved}', '{StatusNames.Nonactionable}')");
                table.HasCheckConstraint(
                    "ck_issues_priority",
                    $"{Db.Issue.Priority} IS NULL OR {Db.Issue.Priority} IN " +
                    $"('{StatusNames.Low}', '{StatusNames.Medium}', '{StatusNames.High}')");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName(Db.Issue.Id);
            entity.Property(e => e.GroupId).HasColumnName(Db.Issue.GroupId);
            entity.Property(e => e.RuleId).HasColumnName(Db.Issue.RuleId);
            entity.Property(e => e.RunId).HasColumnName(Db.Issue.RunId);
            entity.Property(e => e.Status).HasColumnName(Db.Issue.Status);
            entity.Property(e => e.WorkflowStatus).HasColumnName(Db.Issue.WorkflowStatus);
            entity.Property(e => e.AssignedTo).HasColumnName(Db.Issue.AssignedTo);
            entity.Property(e => e.AssignedUserId).HasColumnName(Db.Issue.AssignedUserId);
            entity.Property(e => e.AssignedUserFullName).HasColumnName(Db.Issue.AssignedUserFullName);
            entity.Property(e => e.AssignmentDetachedAtUtc).HasColumnName(Db.Issue.AssignmentDetachedAtUtc);
            entity.HasOne<UserRow>().WithMany().HasForeignKey(e => e.AssignedUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Notes).HasColumnName(Db.Issue.Notes);
            entity.Property(e => e.ExternalNotes).HasColumnName(Db.Issue.ExternalNotes);
            entity.Property(e => e.Priority).HasColumnName(Db.Issue.Priority);
            entity.Property(e => e.ExternalReceivedAtUtc).HasColumnName(Db.Issue.ExternalReceivedAtUtc);
            entity.Property(e => e.SuppressionExpiresAtUtc).HasColumnName(Db.Issue.SuppressionExpiresAtUtc);
            entity.Property(e => e.Context).HasColumnName(Db.Issue.Context).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAtUtc).HasColumnName(Db.Issue.CreatedAtUtc);
            entity.Property(e => e.UpdatedAtUtc).HasColumnName(Db.Issue.UpdatedAtUtc);
            entity.Property(e => e.ActivatedAtUtc).HasColumnName(Db.Issue.ActivatedAtUtc);
            entity.Property(e => e.DeactivatedAtUtc).HasColumnName(Db.Issue.DeactivatedAtUtc);
            entity.Property(e => e.Version).HasColumnName(Db.Issue.Version);
            entity.Property(e => e.OutcomeReasonId).HasColumnName(Db.Issue.OutcomeReasonId);
            entity.Property(e => e.OutcomeReason).HasColumnName(Db.Issue.OutcomeReason)
                .HasMaxLength(CodedValue.MaxNameLength);
            entity.HasOne<CodedValueRow>().WithMany().HasForeignKey(e => e.OutcomeReasonId)
                .HasConstraintName("fk_issues_outcome_reason")
                .OnDelete(DeleteBehavior.Restrict);

            // The end-of-run sweep: every active issue in the group the current run did not touch.
            entity.HasIndex(e => new { e.GroupId, e.Status, e.RunId }).HasDatabaseName("ix_issues_sweep");

            // Default search ordering. Id last so paging stays stable across equal timestamps.
            entity.HasIndex(e => new { e.GroupId, e.UpdatedAtUtc, e.Id })
                .HasDatabaseName("ix_issues_group_updated")
                .IsDescending(false, true, false);

            entity.HasIndex(e => new { e.GroupId, e.RuleId }).HasDatabaseName("ix_issues_group_rule");

            entity.HasIndex(e => e.AssignedTo)
                .HasDatabaseName("ix_issues_assigned")
                .HasFilter($"{Db.Issue.AssignedTo} IS NOT NULL");

            entity.HasIndex(e => e.SuppressionExpiresAtUtc)
                .HasDatabaseName("ix_issues_suppression_expiry")
                .HasFilter($"{Db.Issue.WorkflowStatus} = '{StatusNames.Suppressed}'");

            // jsonb_path_ops is smaller and faster than the default for pure containment (@>),
            // which is all the ContextMatches filter ever asks.
            entity.HasIndex(e => e.Context)
                .HasDatabaseName("ix_issues_context")
                .HasMethod("gin")
                .HasOperators("jsonb_path_ops");
        });

    private static void ConfigureRuleRuns(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<RuleRunRow>(entity =>
        {
            entity.ToTable(Db.RuleRuns, table => table.HasCheckConstraint(
                "ck_rule_runs_state",
                "state IN ('running', 'completed', 'abandoned', 'failed', 'cancelled')"));

            entity.HasKey(e => e.RunId);
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(e => e.FinishedAtUtc).HasColumnName("finished_at_utc");
            entity.Property(e => e.AbandonReason).HasColumnName("abandon_reason");
            entity.Property(e => e.DetectionsProcessed).HasColumnName("detections_processed");
            entity.Property(e => e.IssuesCreated).HasColumnName("issues_created");
            entity.Property(e => e.IssuesReactivated).HasColumnName("issues_reactivated");
            entity.Property(e => e.IssuesUpdated).HasColumnName("issues_updated");
            entity.Property(e => e.IssuesUnchanged).HasColumnName("issues_unchanged");
            entity.Property(e => e.IssuesDeactivated).HasColumnName("issues_deactivated");
            entity.Property(e => e.CurrentStage).HasColumnName("current_stage");
            entity.Property(e => e.RecordsProcessed).HasColumnName("records_processed");
            entity.Property(e => e.RecordsTotal).HasColumnName("records_total");
            entity.Property(e => e.HeartbeatAtUtc).HasColumnName("heartbeat_at_utc");
            entity.Property(e => e.CancellationRequested).HasColumnName("cancellation_requested");
            entity.Property(e => e.FailureMessage).HasColumnName("failure_message");
            entity.Property(e => e.FailureDetail).HasColumnName("failure_detail");
            entity.Property(e => e.Version).HasColumnName("version");

            entity.HasIndex(e => new { e.GroupId, e.StartedAtUtc })
                .HasDatabaseName("ix_rule_runs_group_started")
                .IsDescending(false, true);

            entity.HasIndex(e => e.HeartbeatAtUtc)
                .HasDatabaseName("ix_rule_runs_heartbeat")
                .HasFilter("state = 'running'");

            // The domain checks this, but only the database can enforce it against two
            // schedulers racing to open a run for the same group.
            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ux_rule_runs_one_running_per_group")
                .IsUnique()
                .HasFilter("state = 'running'");
        });

    private static void ConfigureAuditEntries(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AuditEntryRow>(entity =>
        {
            // No foreign key to issues: this is an append-only log that must outlive what it
            // describes, and a constraint would add per-row cost to the bulk audit path.
            entity.ToTable(Db.AuditEntries);

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.ActorDisplayName).HasColumnName("actor_display_name");
            entity.Property(e => e.ActorKind).HasColumnName("actor_kind");
            entity.Property(e => e.AtUtc).HasColumnName("at_utc");
            entity.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb");
            entity.Property(e => e.Subject).HasColumnName("subject");

            entity.HasIndex(e => new { e.IssueId, e.AtUtc, e.Sequence })
                .HasDatabaseName("ix_audit_entries_issue")
                .IsDescending(false, true, true)
                .HasFilter("issue_id IS NOT NULL");

            entity.HasIndex(e => new { e.Subject, e.AtUtc, e.Sequence })
                .HasDatabaseName("ix_audit_entries_subject")
                .IsDescending(false, true, true)
                .HasFilter("subject IS NOT NULL");

            entity.HasIndex(e => new { e.RunId, e.AtUtc, e.Sequence })
                .HasDatabaseName("ix_audit_entries_run")
                .IsDescending(false, true, true)
                .HasFilter("run_id IS NOT NULL");

            // The MI refresh reads, and fingerprints, only the issue entries that change a figure.
            entity.HasIndex(e => e.Sequence)
                .HasDatabaseName("ix_audit_entries_mi")
                .HasFilter(Db.MiAuditFilter);
        });

    private static void ConfigureIssueHistory(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<IssueHistoryRow>(entity =>
        {
            entity.ToTable(Db.IssueHistory, table => table.HasCheckConstraint(
                "ck_issue_history_action_type",
                "action_type IN ('Comment', 'LetterSent', 'PhoneCall')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.ActionType).HasColumnName("action_type");
            entity.Property(e => e.Comments).HasColumnName("comments");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedById).HasColumnName("created_by_id");
            entity.Property(e => e.CreatedByDisplayName).HasColumnName("created_by_display_name");
            entity.Property(e => e.CreatedByKind).HasColumnName("created_by_kind");

            entity.HasOne<IssueRow>()
                .WithMany()
                .HasForeignKey(e => e.IssueId)
                .HasConstraintName("fk_issue_history_issue")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.IssueId, e.CreatedAtUtc, e.Sequence })
                .HasDatabaseName("ix_issue_history_issue")
                .IsDescending(false, true, true);
        });

    private static void ConfigureImportJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportJobRow>(entity =>
        {
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ClientId).HasColumnName("client_id");
            entity.ToTable(Db.ImportJobs, table => table.HasCheckConstraint(
                "ck_import_jobs_status",
                "status IN ('Pending', 'Processing', 'Completed', 'Failed', 'Abandoned')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.FileName).HasColumnName("file_name");
            entity.Property(e => e.BlobKey).HasColumnName("blob_key");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.TotalRows).HasColumnName("total_rows");
            entity.Property(e => e.ProcessedRows).HasColumnName("processed_rows");
            entity.Property(e => e.Matched).HasColumnName("matched");
            entity.Property(e => e.Unmatched).HasColumnName("unmatched");
            entity.Property(e => e.DuplicatesCollapsed).HasColumnName("duplicates_collapsed");
            entity.Property(e => e.Invalid).HasColumnName("invalid");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(e => e.FinishedAtUtc).HasColumnName("finished_at_utc");
            entity.Property(e => e.HeartbeatAtUtc).HasColumnName("heartbeat_at_utc");
            entity.Property(e => e.FailureReason).HasColumnName("failure_reason");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.DatasetId).HasColumnName("dataset_id");
            entity.Property(e => e.UsersCreated).HasColumnName("users_created");

            // Deleting a dataset keeps its jobs and their reports.
            entity.HasOne<SpolDatasetRow>()
                .WithMany()
                .HasForeignKey(e => e.DatasetId)
                .HasConstraintName("fk_import_jobs_dataset")
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.DatasetId)
                .HasDatabaseName("ix_import_jobs_dataset")
                .HasFilter("dataset_id IS NOT NULL");

            // A double-click on Process cannot start two workers over the same group's issues.
            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ux_import_jobs_one_active_per_group")
                .IsUnique()
                .HasFilter("status IN ('Pending', 'Processing')");

            entity.HasIndex(e => new { e.CreatedAtUtc, e.Id })
                .HasDatabaseName("ix_import_jobs_created")
                .IsDescending(true, true);

            entity.HasIndex(e => e.CreatedAtUtc)
                .HasDatabaseName("ix_import_jobs_pending")
                .HasFilter("status = 'Pending'");
        });

        modelBuilder.Entity<ImportJobRejectionRow>(entity =>
        {
            entity.ToTable(Db.ImportJobRejections);

            entity.HasKey(e => e.Sequence);
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.ImportJobId).HasColumnName("import_job_id");
            entity.Property(e => e.RowNumber).HasColumnName("row_number");
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Detail).HasColumnName("detail");

            entity.HasOne<ImportJobRow>()
                .WithMany()
                .HasForeignKey(e => e.ImportJobId)
                .HasConstraintName("fk_import_job_rejections_job")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ImportJobId, e.RowNumber })
                .HasDatabaseName("ix_import_job_rejections_job");
        });
    }
}
