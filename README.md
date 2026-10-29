# ls-cleanse-api

The Cleanse back-end. Cleanse supports the manual migration of livestock holding data from CTS to
SAM: a rules engine runs overnight, compares holding data between the two systems and records each
discrepancy as an issue, and caseworkers triage those issues in the Cleanse front-end,
[ls-cleanse-ui](https://github.com/DEFRA/ls-cleanse-ui).

This service provides:

- the rules engine and its nightly schedule;
- the REST API the front-end uses to search and triage issues;
- the data: issues, their triage state, case history and an append-only audit trail, in
  PostgreSQL.

It is an ASP.NET Core minimal API on .NET 10, built from the CDP .NET backend template, and runs on
DEFRA's Core Delivery Platform (CDP) against Aurora PostgreSQL.

- [What it does](#what-it-does)
  - [Rules](#rules)
- [API](#api)
- [Requirements](#requirements)
- [Running locally](#running-locally)
- [Configuration](#configuration)
- [Database migrations](#database-migrations)
- [Testing](#testing)
- [Operational notes](#operational-notes)
- [Licence](#licence)

## What it does

Each issue has two independent statuses:

| Status           | Set by           | Values                                                                   |
| ---------------- | ---------------- | ------------------------------------------------------------------------ |
| `IssueStatus`    | the rules engine | `Active`, `Inactive`                                                     |
| `WorkflowStatus` | caseworkers      | `Todo`, `InProgress`, `Suppressed`, `Resolved`, `Ignored`, `Nonactionable` |

An issue is identified by the rule and the holding (CPH) it concerns, so the same discrepancy
detected again on a later night updates the same record: its assignee, notes, workflow status and
history are kept. When a run completes, issues in the group that the run did not detect again are
set to `Inactive`, again without changing their workflow status.

Every change to an issue is recorded in an append-only audit trail with before and after values.
Case work — comments, letters sent, phone calls — is recorded separately as issue history. An issue
can be suppressed until a date; a maintenance sweep returns lapsed suppressions to the queue.

### Rules

The service ships one rule group, `cts-sam`:

| Rule | Error code                                                  | Fires when                                                                                                             |
| ---- | ----------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `2`  | `02` `CTS-CPH-missing-in-Sam`                               | A CTS open location's CPH is absent from the SAM `Holding` dataset.                                                    |
| `1`  | `01` `No-cattle-unit-in-Sam`                                | The SAM holding records no animal species code of `BSN`, `CTT` or `BFF`.                                               |
| `3`  | `03` `Holding-name-mismatch-in-Sam`                         | The SAM holding's `FeatureName` is missing, a placeholder, or does not identify the same place as the CTS location name. |
| `10` | `10` `Inconsistent-correspondence-addresses-in-CTS-and-Sam` | No SAM CPH holder's correspondence postcode is the postcode CTS sends correspondence to.                               |

Each issue carries the rule number, error code and description, and the supporting CTS and SAM
fields.

A run starts every day at `Rules:DailyRunAtUtc` (01:00 UTC by default) and can be started on demand
with `POST /v1/runs/trigger`. Only one run per group is in flight at a time; if the service is
running as several instances, one of them starts the run and the others skip it. A run that stops
reporting progress for `Rules:StaleAfter` is abandoned so that it cannot block the next one.

## API

Everything lives under `/v1` and requires authentication; `GET /health` and `GET /v1/openapi.json`
are anonymous. The generated OpenAPI document is checked in at
[artifacts/openapi/Cleanse.json](artifacts/openapi/Cleanse.json).

Two authentication schemes are supported:

- **CDP service-to-service JWT** — used in CDP. The front-end presents an AWS STS web identity token,
  validated against the platform's JWKS and restricted to the service names in
  `Auth:Jwt:AllowedServiceNames`.
- **API key** — header `x-cleanse-api-key`, for local development. Enabled only when
  `Auth:ApiKey:Key` is set.

### Issues

| Method   | Route                             | Purpose                                                                                                        |
| -------- | --------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `GET`    | `/v1/issues`                      | Search and page. Filter by group, rule, run, status, workflow status, assignee, date ranges and a search term. |
| `GET`    | `/v1/issues/{id}`                 | Fetch one issue.                                                                                               |
| `PUT`    | `/v1/issues/{id}/assignee`        | Assign, or unassign.                                                                                           |
| `PUT`    | `/v1/issues/{id}/notes`           | Replace the notes.                                                                                             |
| `PUT`    | `/v1/issues/{id}/workflow-status` | Change the workflow status.                                                                                    |
| `PUT`    | `/v1/issues/{id}/suppression`     | Suppress until a date.                                                                                         |
| `DELETE` | `/v1/issues/{id}/suppression`     | Lift a suppression early.                                                                                      |
| `GET`    | `/v1/issues/{id}/audit`           | The issue's audit trail.                                                                                       |
| `GET`    | `/v1/issues/{id}/history`         | The issue's case history.                                                                                      |
| `POST`   | `/v1/issues/{id}/history`         | Record a case action.                                                                                          |

Updates take an `expectedVersion` and return `409 Conflict` if the issue has changed since it was
read.

### Runs and rules

| Method | Route                           | Purpose                                                                                        |
| ------ | ------------------------------- | ---------------------------------------------------------------------------------------------- |
| `POST` | `/v1/runs/trigger`              | Start the rules engine. Returns `202` with the run id; `409` if a run is already in progress. |
| `GET`  | `/v1/runs`                      | Run history, newest first. Filter by `groupId` and `state`.                                    |
| `GET`  | `/v1/runs/{runId}`              | A run, with its counters and progress.                                                         |
| `POST` | `/v1/runs/{runId}/cancel`       | Ask a running run to stop.                                                                     |
| `GET`  | `/v1/runs/{runId}/audit`        | The run's lifecycle events.                                                                    |
| `GET`  | `/v1/rules`                     | The rules, in execution order.                                                                 |
| `GET`  | `/v1/groups`                    | The rule groups.                                                                               |
| `GET`  | `/v1/groups/{groupId}/summary`  | Issue counts by status.                                                                        |
| `GET`  | `/v1/groups/{groupId}/runs/latest` | The most recent run for a group.                                                            |

The API also exposes the run lifecycle used by the rules engine (`POST /v1/runs`,
`/v1/runs/{runId}/detections`, `/complete` and `/abandon`) and
`POST /v1/maintenance/expire-suppressions`, which sweeps lapsed suppressions.

### Users and diagnostics

| Method | Route                | Purpose                                                                                      |
| ------ | -------------------- | -------------------------------------------------------------------------------------------- |
| `POST` | `/v1/users/logins`   | Record that a user has signed in to the front-end, so that issues can be assigned to them.  |
| `GET`  | `/v1/users`          | The users who have signed in, paged.                                                         |
| `GET`  | `/v1/diagnostics`    | Dependency checks, issue counts and build information, for the front-end's status page.      |

## Requirements

- The .NET 10 SDK.
- Docker, to run PostgreSQL locally and for the PostgreSQL tests.

## Running locally

Run the service directly:

```bash
dotnet run --project Cleanse --launch-profile Cleanse
```

or, on Windows, `./dev-up.ps1`. The service listens on <http://localhost:8085> with the
`Development` settings: a local embedded database, JWT authentication off and the API key
`local-development-key`.

```bash
curl http://localhost:8085/health
curl -H 'x-cleanse-api-key: local-development-key' http://localhost:8085/v1/issues
```

To run the service as it is deployed — the production configuration, PostgreSQL with the schema
applied by Liquibase, and a local S3 emulator — use the Docker stack:

```powershell
./dev-docker-up.ps1            # build, start, wait for health checks and check the API answers
./dev-docker-up.ps1 -NoBuild   # restart without rebuilding the image
./dev-docker-up.ps1 -Follow    # then follow the service's logs
./dev-docker-up.ps1 -Down      # stop; add -Reset to delete the database as well
```

| Service     | Purpose                                                                                           |
| ----------- | ------------------------------------------------------------------------------------------------- |
| `postgres`  | PostgreSQL on `localhost:5432`, with the owner role `ls_cleanse_api_ddl` and service role `ls_cleanse_api`. |
| `liquibase` | Applies `changelog/` as `ls_cleanse_api_ddl`, then exits.                                         |
| `floci`     | A local S3 emulator on `localhost:4566`.                                                          |
| `cleanse`   | This service, on `http://localhost:8085`, with the API key `local-development-key`.               |

The database is kept in a Docker volume between restarts. Plain
`docker compose up --build -d --wait` works too. To move the host ports, set `CLEANSE_PORT`,
`CLEANSE_POSTGRES_PORT` and `CLEANSE_FLOCI_PORT`.

## Configuration

Standard ASP.NET Core configuration: `appsettings.json`, then environment-specific overrides, then
environment variables with `__` for nesting (for example `Storage__Postgres__Host`). In CDP,
non-secret values live in `DEFRA/cdp-app-config` and secrets are set in the CDP Portal.
[scripts/generate-secrets.ps1](scripts/generate-secrets.ps1) generates the secrets this service and
`ls-cleanse-ui` need for an environment.

### Authentication settings (`Auth`)

| Key                       | Default               | Meaning                                                       |
| ------------------------- | --------------------- | ------------------------------------------------------------- |
| `ApiKey:Key`              | _(unset)_             | Shared secret for the `x-cleanse-api-key` header.             |
| `Jwt:Enabled`             | `false`               | Accept CDP service-to-service tokens. Enabled per environment. |
| `Jwt:Audience`            | `ls-cleanse-api`      | Must match the audience the front-end requests.               |
| `Jwt:Issuer`              | from `CDP_JWT_ISSUER` | Injected by CDP.                                              |
| `Jwt:MetadataAddress`     | from `CDP_JWT_OIDC_URL` | Injected by CDP.                                            |
| `Jwt:AllowedServiceNames` | `["ls-cleanse-ui"]`   | Services allowed to call this API.                            |

### Storage settings (`Storage`)

| Key                                               | Default                                                   | Meaning                                              |
| ------------------------------------------------- | --------------------------------------------------------- | ---------------------------------------------------- |
| `Provider`                                        | `Postgres`                                                | Deployed environments use PostgreSQL.                |
| `Postgres:Host` / `Port` / `Database` / `Username` | `localhost` / `5432` / `ls_cleanse_api` / `ls_cleanse_api` | Writer endpoint and service user.                   |
| `Postgres:UseIamAuthentication`                   | `true`                                                    | Connect with a short-lived IAM token.                |
| `Postgres:LocalPassword`                          | _(unset)_                                                 | Local development only, when IAM authentication is off. |
| `Postgres:Region`                                 | `eu-west-2`                                               |                                                      |
| `Postgres:ConnectionTimeoutSeconds`               | `30`                                                      |                                                      |
| `Postgres:CommandTimeoutSeconds`                  | `60`                                                      |                                                      |
| `Postgres:MaxPoolSize`                            | `20`                                                      |                                                      |

### Rules settings (`Rules`)

| Key             | Default    | Meaning                                                       |
| --------------- | ---------- | ------------------------------------------------------------- |
| `Enabled`       | `false`    | Start the nightly schedule. Enabled per environment.          |
| `GroupId`       | `cts-sam`  | The rule group runs are scoped to.                            |
| `DailyRunAtUtc` | `01:00:00` | Time of day, in UTC.                                          |
| `BatchSize`     | `500`      | Records evaluated between writes.                             |
| `StaleAfter`    | `00:15:00` | A run that reports no progress for this long is abandoned.    |

### API settings (`Api`)

| Key                       | Default    | Meaning                                                |
| ------------------------- | ---------- | ------------------------------------------------------ |
| `MaxDetectionsPerRequest` | `1000`     | Detections accepted per batch.                         |
| `RequestTimeoutSeconds`   | `25`       | Requests running longer than this return `504`.        |
| `MaxRequestBodyBytes`     | `10485760` | 10 MB.                                                 |

## Database migrations

The schema is applied by Liquibase from [changelog/db.changelog.xml](changelog/db.changelog.xml),
which CDP runs as the `ls_cleanse_api_ddl` user via _Portal → Apply Changelog_. The running
service connects as `ls_cleanse_api`, has no DDL rights and never migrates the database itself.

The SQL is generated from EF Core migrations rather than hand-written:

```bash
dotnet dotnet-ef migrations add <Name> --project Cleanse.Storage.Postgres --output-dir Schema/Migrations
dotnet dotnet-ef migrations script --idempotent --project Cleanse.Storage.Postgres \
    --output changelog/sql/<nnnn>_<name>.sql
```

To change the schema, add a new migration and a new changeset. Published changesets are never
edited.

## Testing

```bash
dotnet test
```

Tests are xUnit. The domain, API and rules are tested against real implementations; the storage
suites run the same conformance tests against each provider. The PostgreSQL suite starts a
container with Testcontainers, so Docker must be running; without Docker it is skipped, except in
CI (`CI=true`) or when `CLEANSE_REQUIRE_DOCKER=true`, where it fails instead.

The pull-request workflow runs `dotnet test`, and a separate workflow applies the Liquibase
changelog to a fresh database to check that it applies cleanly and matches the EF Core model.

## Operational notes

- `GET /health` is the platform health check.
- Logs are written to stdout as JSON in Elastic Common Schema format, for CDP's OpenSearch.
- Outbound HTTP goes through the CDP proxy.
- The container listens on port `8085`.

## Licence

THIS INFORMATION IS LICENSED UNDER THE CONDITIONS OF THE OPEN GOVERNMENT LICENCE found at:

<http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3>

The following attribution statement MUST be cited in your products and applications when using this
information.

> Contains public sector information licensed under the Open Government license v3

### About the licence

The Open Government Licence (OGL) was developed by the Controller of Her Majesty's Stationery Office
(HMSO) to enable information providers in the public sector to license the use and re-use of their
information under a common open licence.

It is designed to encourage use and re-use of information freely and flexibly, with only a few
conditions.
