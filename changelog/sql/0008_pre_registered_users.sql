START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    DROP INDEX ix_users_scope;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN tenant_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN subject DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN object_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN last_logged_in_at_utc DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN issuer DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN first_logged_in_at_utc DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ALTER COLUMN client_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE users ADD email character varying(320);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    ALTER TABLE issues ADD assignment_detached_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    CREATE INDEX ix_users_object ON users (tenant_id, object_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    CREATE UNIQUE INDEX ux_users_email ON users (email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261109184356_PreRegisteredUsers') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261109184356_PreRegisteredUsers', '10.0.1');
    END IF;
END $EF$;
COMMIT;

