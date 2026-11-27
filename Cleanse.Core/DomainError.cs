namespace Cleanse.Core;

/// <summary>
/// An expected, non-exceptional failure. Codes are stable so a host can map them.
/// <see cref="Subject"/> names the record at fault, such as one issue among several.
/// </summary>
public sealed record DomainError(string Code, string Message, string? Subject = null)
{
    public override string ToString() => $"{Code}: {Message}";
}

public static class ErrorCodes
{
    public const string IssueNotFound = "issue.not_found";
    public const string IssueGroupMismatch = "issue.group_mismatch";
    public const string IssueInvalidTransition = "issue.invalid_transition";
    public const string IssueNotSuppressed = "issue.not_suppressed";
    public const string IssueSuppressionExpiryInPast = "issue.suppression_expiry_in_past";
    public const string IssueConcurrencyConflict = "issue.concurrency_conflict";
    public const string IssueReasonRequired = "issue.reason_required";

    public const string HoldingNotFound = "holding.not_found";
    public const string HoldingConcurrencyConflict = "holding.concurrency_conflict";

    public const string CodedValueNotFound = "coded_value.not_found";
    public const string CodedValueDuplicateName = "coded_value.duplicate_name";

    public const string ActingUserNotAllowed = "auth.acting_user_not_allowed";

    public const string RunNotFound = "run.not_found";
    public const string RunAlreadyInProgress = "run.already_in_progress";
    public const string RunNotRunning = "run.not_running";
    public const string RulesEngineDisabled = "rules.engine_disabled";

    public const string ImportNotFound = "import.not_found";
    public const string ImportAlreadyInProgress = "import.already_in_progress";
    public const string ImportNotPending = "import.not_pending";
    public const string ImportNotProcessing = "import.not_processing";
    public const string ImportAlreadyFinished = "import.already_finished";
    public const string ImportFileInvalid = "import.file_invalid";

    public const string DatasetNotFound = "dataset.not_found";
    public const string DatasetUnauthorised = "dataset.unauthorised";
    public const string DatasetConcurrencyConflict = "dataset.concurrency_conflict";
    public const string DatasetLoadSuperseded = "dataset.load_superseded";
    public const string DatasetProcessing = "dataset.processing";
    public const string DatasetNotReady = "dataset.not_ready";
    public const string DatasetTooLarge = "dataset.too_large";

    public const string ValidationFailed = "validation.failed";
}

public static class DomainErrors
{
    public static DomainError IssueNotFound(string id) =>
        new(ErrorCodes.IssueNotFound, $"Issue '{id}' was not found.");

    public static DomainError IssueGroupMismatch(string id, string expectedGroupId, string actualGroupId) =>
        new(ErrorCodes.IssueGroupMismatch,
            $"Issue '{id}' belongs to group '{actualGroupId}' but the run is for group '{expectedGroupId}'.");

    public static DomainError InvalidTransition(string id, string from, string to) =>
        new(ErrorCodes.IssueInvalidTransition,
            $"Issue '{id}' cannot move from workflow status '{from}' to '{to}'.", id);

    public static DomainError NotSuppressed(string id) =>
        new(ErrorCodes.IssueNotSuppressed, $"Issue '{id}' is not suppressed.", id);

    public static DomainError SuppressionExpiryInPast(string id) =>
        new(ErrorCodes.IssueSuppressionExpiryInPast,
            $"The suppression expiry for issue '{id}' must be in the future.", id);

    public static DomainError ConcurrencyConflict(string id, int expectedVersion, int actualVersion) =>
        new(ErrorCodes.IssueConcurrencyConflict,
            $"Issue '{id}' has version {actualVersion}, not the expected {expectedVersion}.", id);

    public static DomainError ReasonRequired(string id) =>
        new(ErrorCodes.IssueReasonRequired,
            $"Issue '{id}' needs a reason to be marked can't resolve.", id);

    public static DomainError HoldingNotFound(string groupId, string cph) =>
        new(ErrorCodes.HoldingNotFound, $"Group '{groupId}' has no issues for CPH '{cph}'.", cph);

    public static DomainError HoldingConcurrencyConflict(string cph, int expectedVersion, int actualVersion) =>
        new(ErrorCodes.HoldingConcurrencyConflict,
            $"Holding '{cph}' has version {actualVersion}, not the expected {expectedVersion}.", cph);

    public static DomainError CodedValueNotFound(Guid id) =>
        new(ErrorCodes.CodedValueNotFound, $"No list entry has id '{id}'.", id.ToString());

    public static DomainError CodedValueDuplicateName(string list, string name) =>
        new(ErrorCodes.CodedValueDuplicateName, $"The {list} list already has an entry called '{name}'.");

    public static DomainError ActingUserNotAllowed() =>
        new(ErrorCodes.ActingUserNotAllowed, "This caller may not act on behalf of a user.");

    public static DomainError RunNotFound(string runId) =>
        new(ErrorCodes.RunNotFound, $"Run '{runId}' was not found.");

    public static DomainError RunAlreadyInProgress(string groupId, string runId) =>
        new(ErrorCodes.RunAlreadyInProgress,
            $"Group '{groupId}' already has run '{runId}' in progress.");

    public static DomainError RunNotRunning(string runId, string state) =>
        new(ErrorCodes.RunNotRunning, $"Run '{runId}' is '{state}', not running.");

    public static DomainError RulesEngineDisabled() =>
        new(ErrorCodes.RulesEngineDisabled,
            "The rules engine is disabled in this environment. Set Rules:Enabled to start runs.");

    public static DomainError Validation(string message, string? subject = null) =>
        new(ErrorCodes.ValidationFailed, message, subject);

    public static DomainError ImportNotFound(Guid id) =>
        new(ErrorCodes.ImportNotFound, $"Import '{id}' was not found.");

    public static DomainError ImportAlreadyInProgress(string groupId, Guid id) =>
        new(ErrorCodes.ImportAlreadyInProgress,
            $"Group '{groupId}' already has import '{id}' in progress.");

    public static DomainError ImportNotPending(Guid id, string status) =>
        new(ErrorCodes.ImportNotPending, $"Import '{id}' is '{status}', not pending.");

    public static DomainError ImportNotProcessing(Guid id, string status) =>
        new(ErrorCodes.ImportNotProcessing, $"Import '{id}' is '{status}', not processing.");

    public static DomainError ImportAlreadyFinished(Guid id, string status) =>
        new(ErrorCodes.ImportAlreadyFinished, $"Import '{id}' has already finished as '{status}'.");

    public static DomainError ImportFileInvalid(string message) =>
        new(ErrorCodes.ImportFileInvalid, message);

    public static DomainError DatasetNotFound(Guid id) =>
        new(ErrorCodes.DatasetNotFound, $"Dataset '{id}' was not found.");

    /// <summary>The same for an unknown dataset as for a wrong key, so neither gives the other away.</summary>
    public static DomainError DatasetUnauthorised() =>
        new(ErrorCodes.DatasetUnauthorised, "The dataset id or key is not valid.");

    public static DomainError DatasetConcurrencyConflict(Guid id) =>
        new(ErrorCodes.DatasetConcurrencyConflict,
            $"Dataset '{id}' has changed since it was read. Reload it and try again.");

    public static DomainError DatasetLoadSuperseded(Guid id) =>
        new(ErrorCodes.DatasetLoadSuperseded,
            $"Dataset '{id}' has started a newer load, so this load's pages are refused. Run the connector again.");

    public static DomainError DatasetProcessing(Guid id) =>
        new(ErrorCodes.DatasetProcessing, $"Dataset '{id}' is being processed. Try again once it has finished.");

    public static DomainError DatasetNotReady(Guid id, string status) =>
        new(ErrorCodes.DatasetNotReady,
            $"Dataset '{id}' is {status}. Only a Ready dataset, with all of its last load's rows, can be processed.");

    public static DomainError DatasetTooLarge(string message) =>
        new(ErrorCodes.DatasetTooLarge, message);
}
