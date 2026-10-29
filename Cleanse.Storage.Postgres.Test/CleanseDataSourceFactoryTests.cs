using Npgsql;

namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// The connection and command timeouts are what stop a stalled Aurora from filling the pool with
/// requests that never return. Runs without Docker on purpose: it reads the connection string the
/// factory builds rather than opening a connection, so it cannot skip.
/// </summary>
public class CleanseDataSourceFactoryTests
{
    [Fact]
    public void Connections_and_commands_carry_the_configured_timeouts()
    {
        var options = new CleansePostgresOptions
        {
            UseIamAuthentication = false,
            LocalPassword = Guid.NewGuid().ToString("N"),
            ConnectionTimeoutSeconds = 7,
            CommandTimeoutSeconds = 11
        };

        using var dataSource = CleanseDataSourceFactory.Create(options);
        var connectionString = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(7, connectionString.Timeout);
        Assert.Equal(11, connectionString.CommandTimeout);
    }

    [Fact]
    public void The_default_timeouts_are_bounded()
    {
        var options = new CleansePostgresOptions();

        // Wide enough for a cold Aurora Serverless cluster, never unbounded.
        Assert.InRange(options.ConnectionTimeoutSeconds, 1, 60);
        Assert.InRange(options.CommandTimeoutSeconds, 1, 120);
    }
}
