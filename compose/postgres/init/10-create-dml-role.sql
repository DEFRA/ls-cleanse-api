-- Mirrors the CDP two-role model: ls_cleanse_api_ddl owns the schema (POSTGRES_USER),
-- ls_cleanse_api is the login the running service uses. Liquibase changeset 0002 grants it DML
-- on the created objects.
CREATE ROLE ls_cleanse_api LOGIN PASSWORD 'ls_cleanse_api';

GRANT CONNECT ON DATABASE ls_cleanse_api TO ls_cleanse_api;

GRANT USAGE ON SCHEMA public TO ls_cleanse_api;
