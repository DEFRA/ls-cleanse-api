START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE TABLE mi_daily_changes (
        group_id text NOT NULL,
        unit text NOT NULL,
        day date NOT NULL,
        rule_id text NOT NULL,
        status text NOT NULL,
        state text NOT NULL,
        delta integer NOT NULL,
        CONSTRAINT "PK_mi_daily_changes" PRIMARY KEY (group_id, unit, day, rule_id, status, state)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE TABLE mi_events (
        group_id text NOT NULL,
        sequence bigint NOT NULL,
        at_utc timestamp with time zone NOT NULL,
        day date NOT NULL,
        unit text NOT NULL,
        kind text NOT NULL,
        source text NOT NULL,
        issue_id text,
        rule_id text,
        cph text,
        user_id uuid,
        user_name text,
        from_status text,
        to_status text,
        from_workflow text,
        to_workflow text,
        reason_id uuid,
        reason_name text,
        open_change text,
        open_delta integer NOT NULL,
        elapsed_days integer,
        inactive_now boolean,
        actions_since_closed integer,
        CONSTRAINT "PK_mi_events" PRIMARY KEY (group_id, sequence)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE TABLE mi_holding_states (
        group_id text NOT NULL,
        cph text NOT NULL,
        status text NOT NULL,
        has_active_issue boolean NOT NULL,
        open_issues integer NOT NULL,
        oldest_open_raised_at_utc timestamp with time zone,
        oldest_open_raised_day date,
        in_progress_since_utc timestamp with time zone,
        last_worked_at_utc timestamp with time zone,
        assigned_user_id uuid,
        assignee_name text,
        CONSTRAINT "PK_mi_holding_states" PRIMARY KEY (group_id, cph)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE TABLE mi_issue_states (
        group_id text NOT NULL,
        issue_id text NOT NULL,
        rule_id text NOT NULL,
        cph text,
        status text NOT NULL,
        workflow_status text NOT NULL,
        reason_id uuid,
        reason_name text,
        first_raised_at_utc timestamp with time zone NOT NULL,
        first_raised_day date NOT NULL,
        last_changed_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_mi_issue_states" PRIMARY KEY (group_id, issue_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE TABLE mi_refresh (
        group_id text NOT NULL,
        built_at_utc timestamp with time zone NOT NULL,
        checked_at_utc timestamp with time zone NOT NULL,
        fingerprint text NOT NULL,
        version integer NOT NULL,
        duration_milliseconds integer NOT NULL,
        history_from_utc timestamp with time zone,
        issues integer NOT NULL,
        events integer NOT NULL,
        mismatches integer NOT NULL,
        CONSTRAINT "PK_mi_refresh" PRIMARY KEY (group_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE INDEX ix_audit_entries_mi ON audit_entries (sequence) WHERE issue_id IS NOT NULL AND kind IN ('IssueReactivated', 'IssueWorkflowStatusChanged', 'IssueSuppressed', 'IssueSuppressionExpired', 'IssueExternalStateApplied');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE INDEX ix_mi_events_day ON mi_events (group_id, day);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE INDEX ix_mi_events_person ON mi_events (group_id, user_id, day) WHERE source = 'cleanse';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    CREATE INDEX ix_mi_holding_states_open ON mi_holding_states (group_id, oldest_open_raised_at_utc) WHERE status IN ('todo', 'in-progress');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127164859_MiDashboard') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261127164859_MiDashboard', '10.0.1');
    END IF;
END $EF$;
COMMIT;

