using System.Globalization;
using Npgsql;
using OracleRag.Retrieval;
using Pgvector;

namespace OracleRag.Postgres;

/// <summary>Installs the schema as the owner and grants the application role its minimum.</summary>
public sealed class PgSchemaInstaller(NpgsqlDataSource ownerDataSource)
{
    public async Task InstallAsync(string appUser, int dimensions, string embeddingModel, CancellationToken cancellationToken = default)
    {
        SqlIdentifier.Validate(appUser);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 16000);

        using var stream = typeof(PgSchemaInstaller).Assembly.GetManifestResourceStream("OracleRag.Postgres.Sql.01-schema.sql")
            ?? throw new InvalidOperationException("Embedded schema script not found.");
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync(cancellationToken))
            .Replace("{{DIMENSIONS}}", dimensions.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{APP_USER}}", appUser, StringComparison.Ordinal);

        await using var connection = await ownerDataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var ddl = new NpgsqlCommand(sql, connection, transaction))
        {
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var meta = new NpgsqlCommand(
            "INSERT INTO rag.rag_meta (meta_key, meta_value) VALUES ('embedding_model', $1), ('dimensions', $2) ON CONFLICT (meta_key) DO NOTHING",
            connection,
            transaction))
        {
            meta.Parameters.Add(new() { Value = embeddingModel });
            meta.Parameters.Add(new() { Value = dimensions.ToString(CultureInfo.InvariantCulture) });
            await meta.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var check = new NpgsqlCommand("SELECT meta_value FROM rag.rag_meta WHERE meta_key = 'embedding_model'", connection, transaction))
        {
            var stored = (string?)await check.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(stored, embeddingModel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The schema holds vectors from embedding model '{stored}', not '{embeddingModel}'. Re-embed into a new schema.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Owner-side operations: vector index lifecycle and the binary-COPY bulk loader the benchmark uses.</summary>
public sealed class PgVectorAdmin(NpgsqlDataSource ownerDataSource, int maintenanceWorkMemMb = 512, int parallelMaintenanceWorkers = 1) : IVectorIndexAdmin
{
    public const string IndexName = "rag_chunk_vec_ix";

    public async Task CreateIndexAsync(VectorIndexSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await using var connection = await ownerDataSource.OpenConnectionAsync(cancellationToken);
        // HNSW builds fast only while the graph fits in maintenance_work_mem; the worker count matches the CPU limit the
        // benchmark gives both databases (Oracle Free uses at most 2 threads).
        await using (var settings = new NpgsqlCommand(
            $"SET maintenance_work_mem = '{maintenanceWorkMemMb.ToString(CultureInfo.InvariantCulture)}MB'; " +
            $"SET max_parallel_maintenance_workers = {parallelMaintenanceWorkers.ToString(CultureInfo.InvariantCulture)}",
            connection))
        {
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var create = new NpgsqlCommand(CreateIndexSql(spec), connection) { CommandTimeout = 0 };
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string CreateIndexSql(VectorIndexSpec spec)
    {
        spec.Validate();
        var inv = CultureInfo.InvariantCulture;
        return spec.Kind switch
        {
            VectorIndexKind.Hnsw =>
                $"CREATE INDEX {IndexName} ON rag.rag_chunk USING hnsw (embedding vector_cosine_ops) WITH (m = {spec.Neighbors.ToString(inv)}, ef_construction = {spec.EfConstruction.ToString(inv)})",
            VectorIndexKind.Ivf =>
                $"CREATE INDEX {IndexName} ON rag.rag_chunk USING ivfflat (embedding vector_cosine_ops) WITH (lists = {spec.Partitions.ToString(inv)})",
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
    }

    public async Task DropIndexAsync(CancellationToken cancellationToken)
    {
        await using var command = ownerDataSource.CreateCommand($"DROP INDEX IF EXISTS rag.{IndexName}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> IndexSizeBytesAsync(CancellationToken cancellationToken)
    {
        await using var command = ownerDataSource.CreateCommand($"SELECT COALESCE(pg_relation_size(to_regclass('rag.{IndexName}')), 0)");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Binary COPY of documents, then chunks with their vectors.</summary>
    public async Task BulkLoadAsync(IReadOnlyList<(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks)> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await using var connection = await ownerDataSource.OpenConnectionAsync(cancellationToken);
        await using (var docs = await connection.BeginBinaryImportAsync("COPY rag.rag_document (doc_id, title, source, etag) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var (document, chunks) in items)
            {
                await docs.StartRowAsync(cancellationToken);
                await docs.WriteAsync(document.Id, cancellationToken);
                await docs.WriteAsync(document.Title, cancellationToken);
                await docs.WriteAsync(document.Source, cancellationToken);
                await docs.WriteAsync(ContentEtag.Compute(document, chunks), cancellationToken);
            }

            await docs.CompleteAsync(cancellationToken);
        }

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY rag.rag_chunk (chunk_id, doc_id, ord, heading, body, content_hash, embedding) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var (document, chunks) in items)
        {
            foreach (var chunk in chunks)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(chunk.Id, cancellationToken);
                await writer.WriteAsync(document.Id, cancellationToken);
                await writer.WriteAsync(chunk.Ord, cancellationToken);
                await writer.WriteAsync(chunk.Heading, cancellationToken);
                await writer.WriteAsync(chunk.Text, cancellationToken);
                await writer.WriteAsync(chunk.ContentHash, cancellationToken);
                await writer.WriteAsync(new Vector(chunk.Embedding), "vector", cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }

    public async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        await using var command = ownerDataSource.CreateCommand("ANALYZE rag.rag_document; ANALYZE rag.rag_chunk");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await DropIndexAsync(cancellationToken);
        await using var command = ownerDataSource.CreateCommand("TRUNCATE rag.rag_chunk, rag.rag_document");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
