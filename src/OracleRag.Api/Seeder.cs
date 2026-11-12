using OracleRag.Ingestion;
using OracleRag.Oracle;
using OracleRag.Postgres;
using OracleRag.Retrieval;
using OracleRag.Synthetic;

namespace OracleRag.Api;

public sealed class RagOptions
{
    public int Dimensions { get; set; } = 384;

    public string DefaultStore { get; set; } = "oracle";

    public bool EnableMemoryStore { get; set; }

    public OracleOptions Oracle { get; set; } = new();

    public PostgresOptions Postgres { get; set; } = new();

    public SeedOptions Seed { get; set; } = new();

    public sealed class OracleOptions
    {
        public string SchemaOwner { get; set; } = "RAG_OWNER";

        public string AppUser { get; set; } = "RAG_APP";
    }

    public sealed class PostgresOptions
    {
        public string AppUser { get; set; } = "rag_app";
    }

    public sealed class SeedOptions
    {
        public int Entities { get; set; } = 40;

        public int Seed { get; set; } = 2026;

        public bool CreateHnswIndex { get; set; } = true;
    }
}

/// <summary>
/// Installs both schemas as their owners, writes the synthetic corpus into each store through the same path the API
/// uses (Oracle: the duality view), and builds an HNSW index on both. Re-runnable: documents are upserts, the index is
/// rebuilt.
/// </summary>
internal static partial class Seeder
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Store}: {Documents} documents, {Chunks} chunks, HNSW index {Index}")]
    private static partial void LogSeeded(ILogger logger, string store, int documents, long chunks, bool index);

    public static async Task<int> RunAsync(IConfiguration configuration, RagOptions options, RagService rag, ILogger logger)
    {
        var corpus = CorpusGenerator.Generate(options.Seed.Entities, options.Seed.Seed);
        var model = rag.Embedder.ModelId;
        var targets = new List<(IVectorStore Store, IVectorIndexAdmin Admin)>();

        var oracleOwner = configuration.GetConnectionString("OracleOwner");
        var oracleApp = configuration.GetConnectionString("OracleApp");
        if (!string.IsNullOrWhiteSpace(oracleOwner) && !string.IsNullOrWhiteSpace(oracleApp))
        {
            await new OracleSchemaInstaller(oracleOwner).InstallAsync(options.Oracle.AppUser, options.Dimensions, model);
            targets.Add((new OracleVectorStore(oracleApp, options.Oracle.SchemaOwner, options.Dimensions), new OracleVectorAdmin(oracleOwner)));
        }

        var postgresOwner = configuration.GetConnectionString("PostgresOwner");
        var postgresApp = configuration.GetConnectionString("PostgresApp");
        if (!string.IsNullOrWhiteSpace(postgresOwner) && !string.IsNullOrWhiteSpace(postgresApp))
        {
            var ownerSource = PgVectorStore.CreateDataSource(postgresOwner);
            await new PgSchemaInstaller(ownerSource).InstallAsync(options.Postgres.AppUser, options.Dimensions, model);
            targets.Add((new PgVectorStore(PgVectorStore.CreateDataSource(postgresApp), options.Dimensions), new PgVectorAdmin(ownerSource)));
        }

        if (targets.Count == 0)
        {
            logger.LogError("seed needs at least one pair of owner and application connection strings");
            return 1;
        }

        foreach (var (store, admin) in targets)
        {
            foreach (var document in corpus.Documents)
            {
                await rag.IngestAsync(store, document, null, CancellationToken.None);
            }

            if (options.Seed.CreateHnswIndex)
            {
                await admin.DropIndexAsync(CancellationToken.None);
                await admin.CreateIndexAsync(VectorIndexSpec.Hnsw(), CancellationToken.None);
            }

            var chunks = await store.CountChunksAsync(CancellationToken.None);
            LogSeeded(logger, store.Name, corpus.Documents.Count, chunks, options.Seed.CreateHnswIndex);
        }

        return 0;
    }
}
