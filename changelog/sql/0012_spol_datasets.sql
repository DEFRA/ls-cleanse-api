START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    ALTER TABLE import_jobs ALTER COLUMN blob_key DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    ALTER TABLE import_jobs ADD dataset_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    ALTER TABLE import_jobs ADD users_created integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    CREATE TABLE spol_datasets (
        id uuid NOT NULL,
        name character varying(100) NOT NULL,
        key_hash character varying(64) NOT NULL,
        status character varying(16) NOT NULL,
        row_count integer NOT NULL,
        expected_rows integer,
        page_count integer,
        current_load_id uuid,
        load_started_at_utc timestamp with time zone,
        last_loaded_at_utc timestamp with time zone,
        created_by_id text NOT NULL,
        created_by_name text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        version integer NOT NULL,
        CONSTRAINT "PK_spol_datasets" PRIMARY KEY (id),
        CONSTRAINT ck_spol_datasets_status CHECK (status IN ('Empty', 'Loading', 'Ready'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    CREATE TABLE spol_dataset_items (
        dataset_id uuid NOT NULL,
        item_id integer NOT NULL,
        data jsonb NOT NULL,
        CONSTRAINT "PK_spol_dataset_items" PRIMARY KEY (dataset_id, item_id),
        CONSTRAINT fk_spol_dataset_items_dataset FOREIGN KEY (dataset_id) REFERENCES spol_datasets (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    CREATE INDEX ix_import_jobs_dataset ON import_jobs (dataset_id) WHERE dataset_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    CREATE INDEX ix_spol_datasets_created ON spol_datasets (created_at_utc DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    ALTER TABLE import_jobs ADD CONSTRAINT fk_import_jobs_dataset FOREIGN KEY (dataset_id) REFERENCES spol_datasets (id) ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127121933_SpolDatasets') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261127121933_SpolDatasets', '10.0.1');
    END IF;
END $EF$;
COMMIT;

