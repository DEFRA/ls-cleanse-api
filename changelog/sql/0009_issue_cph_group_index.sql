START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119102848_IssueCphGroupIndex') THEN
    CREATE INDEX ix_issues_group_cph ON issues (group_id, (COALESCE(context ->> 'cph', '')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119102848_IssueCphGroupIndex') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261119102848_IssueCphGroupIndex', '10.0.1');
    END IF;
END $EF$;
COMMIT;

