using DotNet.Testcontainers.Configurations;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using OracleRag.Embeddings;
using OracleRag.Oracle;
using OracleRag.Postgres;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(OracleRag.IntegrationTests.OracleFixture))]
[assembly: AssemblyFixture(typeof(OracleRag.IntegrationTests.PostgresFixture))]

namespace OracleRag.IntegrationTests;

internal static class InitScript
{
    public const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    public static byte[] Load(string database) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "init", database, "01-setup.sh"));
}

/// <summary>
/// One Oracle AI Database Free container per run, created by the same init script docker compose uses (vector pool,
/// RAG_OWNER, RAG_APP). The schema is installed as the owner; tests use the application account unless they test the
/// owner side.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    // Test-only credentials for a throwaway container.
    private const string OwnerPassword = "OwnerTest1";
    private const string AppPassword = "AppTest1";

    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithEnvironment("VECTOR_MEMORY_SIZE", "256M")
        .WithResourceMapping(InitScript.Load("oracle"), "/container-entrypoint-initdb.d/01-setup.sh", fileMode: InitScript.Executable)
        .WithLabel("project", "oracle23ai-rag-tests")
        .Build();

    public static LexicalProjectionEmbedder Embedder { get; } = new();

    public string OwnerConnectionString => WithCredentials("rag_owner", OwnerPassword);

    public string AppConnectionString => WithCredentials("rag_app", AppPassword);

    public OracleVectorStore Store => new(AppConnectionString, "RAG_OWNER", Embedder.Dimensions);

    public OracleVectorAdmin Admin => new(OwnerConnectionString);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await new OracleSchemaInstaller(OwnerConnectionString).InstallAsync("RAG_APP", Embedder.Dimensions, Embedder.ModelId);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private string WithCredentials(string user, string password) =>
        new OracleConnectionStringBuilder(_container.GetConnectionString())
        {
            UserID = user,
            Password = password,
            ConnectionTimeout = 60,
        }.ConnectionString;
}

/// <summary>pgvector 0.8.1 on PostgreSQL 17, created by the same init script docker compose uses.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string OwnerPassword = "OwnerTest1";
    private const string AppPassword = "AppTest1";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.1-pg17")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(InitScript.Load("postgres"), "/docker-entrypoint-initdb.d/01-setup.sh", fileMode: InitScript.Executable)
        .WithLabel("project", "oracle23ai-rag-tests")
        .Build();

    private NpgsqlDataSource? _owner;
    private NpgsqlDataSource? _app;

    public NpgsqlDataSource OwnerDataSource => _owner ?? throw new InvalidOperationException("Not started.");

    public NpgsqlDataSource AppDataSource => _app ?? throw new InvalidOperationException("Not started.");

    public PgVectorStore Store => new(AppDataSource, OracleFixture.Embedder.Dimensions);

    public PgVectorAdmin Admin => new(OwnerDataSource, maintenanceWorkMemMb: 128);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _owner = PgVectorStore.CreateDataSource(WithCredentials("rag_owner", OwnerPassword));
        _app = PgVectorStore.CreateDataSource(WithCredentials("rag_app", AppPassword));
        await new PgSchemaInstaller(_owner).InstallAsync("rag_app", OracleFixture.Embedder.Dimensions, OracleFixture.Embedder.ModelId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private string WithCredentials(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = user,
            Password = password,
            Timeout = 60,
        }.ConnectionString;
}
