using System.Data;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using OracleRag.Retrieval;

namespace OracleRag.Oracle;

/// <summary>Owner-side operations: vector index lifecycle and the bulk loader the benchmark uses.</summary>
public sealed class OracleVectorAdmin(string ownerConnectionString) : IVectorIndexAdmin
{
    public const string IndexName = "RAG_CHUNK_VEC_IX";
    private const int IndexDoesNotExist = 1418;

    public async Task CreateIndexAsync(VectorIndexSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await ExecuteAsync(CreateIndexSql(spec), cancellationToken);
    }

    /// <summary>
    /// DDL for the index. No <c>TARGET ACCURACY</c> is set at build time: the benchmark controls accuracy per query
    /// with <c>EFSEARCH</c> / <c>NEIGHBOR PARTITION PROBES</c>, the same knobs pgvector exposes.
    /// </summary>
    internal static string CreateIndexSql(VectorIndexSpec spec)
    {
        spec.Validate();
        var inv = CultureInfo.InvariantCulture;
        return spec.Kind switch
        {
            VectorIndexKind.Hnsw => $"""
                CREATE VECTOR INDEX {IndexName} ON rag_chunk (embedding)
                ORGANIZATION INMEMORY NEIGHBOR GRAPH
                DISTANCE COSINE
                PARAMETERS (TYPE HNSW, NEIGHBORS {spec.Neighbors.ToString(inv)}, EFCONSTRUCTION {spec.EfConstruction.ToString(inv)})
                """,
            VectorIndexKind.Ivf => $"""
                CREATE VECTOR INDEX {IndexName} ON rag_chunk (embedding)
                ORGANIZATION NEIGHBOR PARTITIONS
                DISTANCE COSINE
                PARAMETERS (TYPE IVF, NEIGHBOR PARTITIONS {spec.Partitions.ToString(inv)})
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
    }

    public async Task DropIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync($"DROP INDEX {IndexName}", cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == IndexDoesNotExist)
        {
            // Nothing to drop.
        }
    }

    /// <summary>
    /// HNSW lives in the SGA vector pool, so its size is the pool memory the index's owner holds
    /// (<c>V$VECTOR_MEMORY_POOL</c> is instance-wide; the benchmark has one index at a time). IVF is a pair of
    /// ordinary tables, measured by their segments.
    /// </summary>
    public async Task<long> IndexSizeBytesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var kind = connection.CreateCommand();
        kind.CommandText = $"SELECT index_subtype FROM user_indexes WHERE index_name = '{IndexName}'";
        var organization = await kind.ExecuteScalarAsync(cancellationToken) as string;
        if (organization is null)
        {
            return 0;
        }

        await using var size = connection.CreateCommand();
        size.CommandText = organization.Contains("GRAPH", StringComparison.OrdinalIgnoreCase)
            ? "SELECT NVL(SUM(used_bytes), 0) FROM v$vector_memory_pool"
            : $"""
              SELECT NVL(SUM(s.bytes), 0) FROM user_segments s
              WHERE s.segment_name IN (SELECT table_name FROM user_tables WHERE table_name LIKE 'VECTOR$' || '{IndexName}' || '%')
              """;
        return Convert.ToInt64(await size.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Bulk load for benchmarks: array-bound inserts straight into the base tables (documents, then chunks with their
    /// vectors), one commit per batch. The duality view is the API's write path; this is the "load a corpus" path.
    /// </summary>
    public async Task BulkLoadAsync(IReadOnlyList<(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks)> items, int batchChunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        var docs = new List<DocumentRecord>();
        var chunks = new List<(string DocId, ChunkRecord Chunk)>();
        foreach (var (document, documentChunks) in items)
        {
            docs.Add(document);
            chunks.AddRange(documentChunks.Select(c => (document.Id, c)));
            if (chunks.Count >= batchChunks)
            {
                await FlushAsync(connection, docs, chunks, cancellationToken);
            }
        }

        await FlushAsync(connection, docs, chunks, cancellationToken);
    }

    /// <summary>Gathers optimizer statistics after a bulk load so plans are not built on an empty-table estimate.</summary>
    public async Task GatherStatisticsAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync("BEGIN DBMS_STATS.GATHER_SCHEMA_STATS(USER); END;", cancellationToken);

    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await DropIndexAsync(cancellationToken);
        await ExecuteAsync("DELETE FROM rag_chunk", cancellationToken);
        await ExecuteAsync("DELETE FROM rag_document", cancellationToken);
    }

    private static async Task FlushAsync(OracleConnection connection, List<DocumentRecord> docs, List<(string DocId, ChunkRecord Chunk)> chunks, CancellationToken cancellationToken)
    {
        if (docs.Count == 0)
        {
            return;
        }

        await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var insertDocs = connection.CreateCommand())
        {
            insertDocs.BindByName = true;
            insertDocs.CommandText = "INSERT INTO rag_document (doc_id, title, source) VALUES (:id, :title, :source)";
            insertDocs.ArrayBindCount = docs.Count;
            insertDocs.Parameters.Add("id", OracleDbType.Varchar2, docs.Select(d => d.Id).ToArray(), ParameterDirection.Input);
            insertDocs.Parameters.Add("title", OracleDbType.Varchar2, docs.Select(d => d.Title).ToArray(), ParameterDirection.Input);
            insertDocs.Parameters.Add("source", OracleDbType.Varchar2, docs.Select(d => d.Source).ToArray(), ParameterDirection.Input);
            await insertDocs.ExecuteNonQueryAsync(cancellationToken);
        }

        if (chunks.Count > 0)
        {
            await using var insertChunks = connection.CreateCommand();
            insertChunks.BindByName = true;
            insertChunks.CommandText = """
                INSERT INTO rag_chunk (chunk_id, doc_id, ord, heading, body, content_hash, embedding)
                VALUES (:id, :doc, :ord, :heading, :body, :hash, :emb)
                """;
            insertChunks.ArrayBindCount = chunks.Count;
            insertChunks.Parameters.Add("id", OracleDbType.Varchar2, chunks.Select(c => c.Chunk.Id).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("doc", OracleDbType.Varchar2, chunks.Select(c => c.DocId).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("ord", OracleDbType.Int32, chunks.Select(c => c.Chunk.Ord).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("heading", OracleDbType.Varchar2, chunks.Select(c => c.Chunk.Heading).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("body", OracleDbType.Varchar2, chunks.Select(c => c.Chunk.Text).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("hash", OracleDbType.Varchar2, chunks.Select(c => c.Chunk.ContentHash).ToArray(), ParameterDirection.Input);
            insertChunks.Parameters.Add("emb", OracleDbType.Vector_Float32, chunks.Select(c => new OracleVector(c.Chunk.Embedding)).ToArray(), ParameterDirection.Input);
            await insertChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        docs.Clear();
        chunks.Clear();
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
