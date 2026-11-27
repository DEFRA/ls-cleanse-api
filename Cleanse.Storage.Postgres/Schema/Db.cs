namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// Physical names, spelled once. The raw-SQL paths and the EF configuration both read from here,
/// so a rename cannot leave one of them behind.
/// </summary>
internal static class Db
{
    public const string Issues = "issues";
    public const string RuleRuns = "rule_runs";
    public const string AuditEntries = "audit_entries";
    public const string IssueHistory = "issue_history";
    public const string ImportJobs = "import_jobs";
    public const string ImportJobRejections = "import_job_rejections";
    public const string DataResets = "data_resets";
    public const string Holdings = "holdings";
    public const string HoldingNotes = "holding_notes";
    public const string HoldingActions = "holding_actions";
    public const string CodedValues = "coded_values";
    public const string ServiceSettings = "service_settings";
    public const string SpolDatasets = "spol_datasets";
    public const string SpolDatasetItems = "spol_dataset_items";

    public static class Issue
    {
        public const string Id = "id";
        public const string GroupId = "group_id";
        public const string RuleId = "rule_id";
        public const string RunId = "run_id";
        public const string Status = "status";
        public const string WorkflowStatus = "workflow_status";
        public const string AssignedTo = "assigned_to";
        public const string AssignedUserId = "assigned_user_id";
        public const string AssignedUserFullName = "assigned_user_full_name";
        public const string AssignmentDetachedAtUtc = "assignment_detached_at_utc";
        public const string Notes = "notes";
        public const string ExternalNotes = "external_notes";
        public const string Priority = "priority";
        public const string ExternalReceivedAtUtc = "external_received_at_utc";
        public const string SuppressionExpiresAtUtc = "suppression_expires_at_utc";
        public const string Context = "context";
        public const string CreatedAtUtc = "created_at_utc";
        public const string UpdatedAtUtc = "updated_at_utc";
        public const string ActivatedAtUtc = "activated_at_utc";
        public const string DeactivatedAtUtc = "deactivated_at_utc";
        public const string Version = "version";
        public const string OutcomeReasonId = "outcome_reason_id";
        public const string OutcomeReason = "outcome_reason";

        /// <summary>Insert order. The parameter binding is built from the same order.</summary>
        public static readonly string[] All =
        [
            Id, GroupId, RuleId, RunId, Status, WorkflowStatus, AssignedTo, Notes,
            ExternalNotes, Priority, ExternalReceivedAtUtc, SuppressionExpiresAtUtc,
            Context, CreatedAtUtc, UpdatedAtUtc, ActivatedAtUtc, DeactivatedAtUtc, Version,
            AssignedUserId, AssignedUserFullName, AssignmentDetachedAtUtc, OutcomeReasonId, OutcomeReason
        ];

        public static readonly string AllColumns = string.Join(", ", All);

        /// <summary>The CPH an issue belongs to, as the grouped search and its index spell it.</summary>
        public const string Cph = "COALESCE(context ->> 'cph', '')";
    }

    public static class Holding
    {
        public const string GroupId = "group_id";
        public const string Cph = "cph";
        public const string AssignedTo = "assigned_to";
        public const string AssignedUserId = "assigned_user_id";
        public const string AssignedUserFullName = "assigned_user_full_name";
        public const string AssignmentDetachedAtUtc = "assignment_detached_at_utc";
        public const string CreatedAtUtc = "created_at_utc";
        public const string UpdatedAtUtc = "updated_at_utc";
        public const string Version = "version";
    }
}
