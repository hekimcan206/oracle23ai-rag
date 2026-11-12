using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using OracleRag.Retrieval;

namespace OracleRag.Api.Infrastructure;

/// <summary>The stores this instance serves, by name. Unknown names are a 404, not a fallback.</summary>
internal sealed class StoreRegistry(IEnumerable<IVectorStore> stores, string defaultStore)
{
    private readonly Dictionary<string, IVectorStore> _stores = stores.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _stores.Keys;

    public string DefaultStore { get; } = defaultStore;

    public IVectorStore? Find(string name) => _stores.GetValueOrDefault(name);
}

/// <summary>Readiness: can the application account open a session?</summary>
internal sealed class OracleHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM dual";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OracleException ex)
        {
            return HealthCheckResult.Unhealthy("Oracle unavailable", ex);
        }
    }
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unavailable", ex);
        }
    }
}
