START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127101940_ServiceSettings') THEN
    CREATE TABLE service_settings (
        id integer NOT NULL,
        minimum_issue_age_days integer NOT NULL,
        CONSTRAINT "PK_service_settings" PRIMARY KEY (id),
        CONSTRAINT ck_service_settings_minimum_issue_age_days CHECK (minimum_issue_age_days >= 0),
        CONSTRAINT ck_service_settings_one_row CHECK (id = 1)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127101940_ServiceSettings') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261127101940_ServiceSettings', '10.0.1');
    END IF;
END $EF$;
COMMIT;

