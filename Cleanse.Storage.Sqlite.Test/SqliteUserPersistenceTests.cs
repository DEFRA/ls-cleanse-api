using System.Text.RegularExpressions;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Cleanse.Storage.Sqlite.Stores;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Test;

public sealed class SqliteUserPersistenceTests
{
    private static readonly Guid LegacyUserId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public async Task Existing_local_database_is_upgraded_idempotently_and_users_survive_reopening()
    {
        Directory.CreateDirectory("artifacts");
        var path = Path.GetFullPath(Path.Combine("artifacts", $"users-{Guid.NewGuid():N}.sqlite"));
        var options = new CleanseSqliteOptions { DataSource = path };
        try
        {
            await using (var connection = new SqliteConnection(options.BuildConnectionString()))
            {
                await connection.OpenAsync();
                await using var stream = typeof(CleanseSqliteDatabase).Assembly.GetManifestResourceStream(
                    "Cleanse.Storage.Sqlite.Schema.schema.sql")!;
                using var reader = new StreamReader(stream);
                var schema = await reader.ReadToEndAsync();
                schema = schema[..schema.IndexOf("CREATE TABLE IF NOT EXISTS users (", StringComparison.Ordinal)];
                schema = Regex.Replace(schema, @"^\s*assign(ed_user_(id|full_name)|ment_detached_at_utc).*\r?\n", "", RegexOptions.Multiline);
                schema = Regex.Replace(schema, @"^\s*(tenant_id|client_id).*\r?\n", "", RegexOptions.Multiline);
                // The users table as the first release of the directory created it.
                schema += """
                    CREATE TABLE users (
                        id TEXT NOT NULL PRIMARY KEY, issuer TEXT NOT NULL, subject TEXT NOT NULL,
                        tenant_id TEXT NOT NULL, object_id TEXT NOT NULL, client_id TEXT NOT NULL,
                        full_name TEXT NOT NULL, first_logged_in_at_utc TEXT NOT NULL,
                        last_logged_in_at_utc TEXT NOT NULL);
                    CREATE UNIQUE INDEX ux_users_identity ON users (issuer, client_id, subject);
                    CREATE INDEX ix_users_scope ON users (tenant_id, client_id, id);
                    """;
                await using var create = connection.CreateCommand();
                create.CommandText = schema;
                await create.ExecuteNonQueryAsync();
                create.CommandText = """
                    INSERT INTO issues
                        (id, group_id, rule_id, run_id, status, workflow_status, assigned_to,
                         created_at_utc, updated_at_utc, activated_at_utc, version)
                    VALUES ('rule:legacy', 'group', 'rule', 'run', 'active', 'todo', 'legacy-name',
                            $now, $now, $now, 1);
                    INSERT INTO users VALUES
                        ($legacy, 'https://issuer.example.test', 'legacy-subject', 'tenant', 'legacy-object',
                         'client', 'Legacy Person', $now, $now);
                    """;
                create.Parameters.AddWithValue("$now", DateTimeOffset.UnixEpoch.ToText());
                create.Parameters.AddWithValue("$legacy", LegacyUserId.ToString());
                await create.ExecuteNonQueryAsync();
            }

            var login = new KnownUser(Guid.NewGuid(), "https://issuer.example.test", "subject", "tenant", "object",
                "client", "Alex Example", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "alex@example.test");
            await using (var database = new CleanseSqliteDatabase(options))
            {
                await database.InitialiseAsync();
                await database.InitialiseAsync();
                var issues = new SqliteIssueStore(database, options);
                var legacy = await issues.GetAsync(IssueId.From("rule:legacy"));
                Assert.Equal("legacy-name", legacy!.AssignedTo);
                Assert.Null(legacy.AssignedUserId);
                Assert.Null(legacy.AssignmentDetachedAtUtc);
                var users = new SqliteUserStore(database);
                var legacyUser = (await users.GetAsync(LegacyUserId))!;
                Assert.Equal("Legacy Person", legacyUser.FullName);
                Assert.Null(legacyUser.Email);
                Assert.True((await users.RegisterAsync([KnownUser.Unclaimed("alex@example.test", "Alex")]))[0].Created);
                login = await users.UpsertLoginAsync(login, ["alex@example.test"]);
                legacy.AssignKnownUser(login, DateTimeOffset.UnixEpoch);
                await issues.SaveAsync(legacy, 1);
            }

            await using (var reopened = new CleanseSqliteDatabase(options))
            {
                await reopened.InitialiseAsync();
                var users = new SqliteUserStore(reopened);
                Assert.Equal(login, await users.GetAsync(login.Id));
                var issue = await new SqliteIssueStore(reopened, options).GetAsync(IssueId.From("rule:legacy"));
                Assert.Equal(login.Id, issue!.AssignedUserId);
                Assert.Equal(login.FullName, issue.AssignedUserFullName);
                Assert.Equal(DateTimeOffset.UnixEpoch, issue.AssignmentDetachedAtUtc);
                var relogin = await users.UpsertLoginAsync(login with
                {
                    Id = Guid.NewGuid(), LastLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1)
                }, []);
                Assert.Equal(login.Id, relogin.Id);
                Assert.Equal(login.FirstLoggedInAtUtc, relogin.FirstLoggedInAtUtc);
                await new SqliteDataResetStore(reopened).ResetAsync(null, "test", DateTimeOffset.UnixEpoch);
                Assert.Equal(2, (await users.SearchAsync(PageRequest.Default)).TotalCount);
            }
        }
        finally
        {
            using var connection = new SqliteConnection(options.BuildConnectionString());
            SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
