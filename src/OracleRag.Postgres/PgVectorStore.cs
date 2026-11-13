using System.Globalization;
using System.Text.Json;
using Npgsql;
using OracleRag.Retrieval;
using Pgvector;

namespace OracleRag.Postgres;

/// <summary>
/// pgvector store with the same contract and schema shape as the Oracle store. The document JSON is assembled by a
/// read-only <c>jsonb_agg</c> view; writes are an explicit application transaction with a row lock, because PostgreSQL
/// has no writable equivalent of a duality view.
/// </summary>
public sealed class PgVectorStore(NpgsqlDataSource dataSource, int dimensions) : IVectorStore
{
    public string Name => "postgres";

    /// <summary>Builds a data source with the pgvector type mapping registered.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // Npgsql prefers GSS encryption by default and probes for libgssapi, which the chiseled image does not ship
        // (it logs a native-loader error on every start). This service authenticates with passwords only.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        builder.UseVector();
        return builder.Build();
    }

    public async Task<WriteResult> UpsertAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string? expectedEtag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.Length != dimensions)
            {
                throw new ArgumentException($"Chunk {chunk.Id} has {chunk.Embedding.Length} dimensions; the schema expects {dimensions}.");
            }
        }

        var etag = ContentEtag.Compute(document, chunks);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // INSERT ... ON CONFLICT DO NOTHING followed by SELECT ... FOR UPDATE serialises concurrent writers of the same
        // document on its row lock, including two writers racing to create it.
        bool created;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO rag.rag_document (doc_id, title, source, etag) VALUES ($1, $2, $3, $4) ON CONFLICT (doc_id) DO NOTHING",
            connection, transaction))
        {
            insert.Parameters.Add(new() { Value = document.Id });
            insert.Parameters.Add(new() { Value = document.Title });
            insert.Parameters.Add(new() { Value = document.Source });
            insert.Parameters.Add(new() { Value = etag });
            created = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        if (created && expectedEtag is not null)
        {
            // A conditional write against a document that did not exist is a conflict, not a create.
            throw new EtagMismatchException(document.Id);
        }

        if (!created)
        {
            await using var current = new NpgsqlCommand("SELECT etag FROM rag.rag_document WHERE doc_id = $1 FOR UPDATE", connection, transaction);
            current.Parameters.Add(new() { Value = document.Id });
            var stored = (string?)await current.ExecuteScalarAsync(cancellationToken);
            if (expectedEtag is not null && !string.Equals(stored, expectedEtag, StringComparison.Ordinal))
            {
                throw new EtagMismatchException(document.Id);
            }

            await using var replace = new NpgsqlBatch(connection, transaction);
            replace.BatchCommands.Add(new("UPDATE rag.rag_document SET title = $2, source = $3, etag = $4 WHERE doc_id = $1")
            {
                Parameters = { new() { Value = document.Id }, new() { Value = document.Title }, new() { Value = document.Source }, new() { Value = etag } },
            });
            replace.BatchCommands.Add(new("DELETE FROM rag.rag_chunk WHERE doc_id = $1") { Parameters = { new() { Value = document.Id } } });
            await replace.ExecuteNonQueryAsync(cancellationToken);
        }

        if (chunks.Count > 0)
        {
            await using var insertChunks = new NpgsqlCommand(
                """
                INSERT INTO rag.rag_chunk (chunk_id, doc_id, ord, heading, body, content_hash, embedding)
                SELECT u.chunk_id, $1, u.ord, u.heading, u.body, u.content_hash, u.embedding
                FROM unnest($2::text[], $3::int[], $4::text[], $5::text[], $6::text[], $7::vector[])
                     AS u (chunk_id, ord, heading, body, content_hash, embedding)
                """,
                connection,
                transaction);
            insertChunks.Parameters.Add(new() { Value = document.Id });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => c.Id).ToArray() });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => c.Ord).ToArray() });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => c.Heading).ToArray() });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => c.Text).ToArray() });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => c.ContentHash).ToArray() });
            insertChunks.Parameters.Add(new() { Value = chunks.Select(c => new Vector(c.Embedding)).ToArray() });
            await insertChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new WriteResult(etag, created);
    }

    public async Task<DocumentView?> GetAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT data::text FROM rag.rag_document_json WHERE doc_id = $1");
        command.Parameters.Add(new() { Value = documentId });
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return json is null ? null : ParseDocument(json);
    }

    public async Task<bool> DeleteAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM rag.rag_document WHERE doc_id = $1");
        command.Parameters.Add(new() { Value = documentId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(dimensions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // One round trip: transaction-local planner settings followed by the query. The statements of a batch share
        // one implicit transaction, so set_config(..., true) applies to the SELECT and is gone afterwards.
        await using var batch = new NpgsqlBatch(connection);
        foreach (var (name, value) in Settings(query))
        {
            batch.BatchCommands.Add(new("SELECT set_config($1, $2, true)") { Parameters = { new() { Value = name }, new() { Value = value } } });
        }

        var search = new NpgsqlBatchCommand(SearchSql(query.Mode));
        search.Parameters.Add(new() { Value = new Vector(query.Vector) });
        search.Parameters.Add(new() { Value = query.K });
        batch.BatchCommands.Add(search);

        var hits = new List<SearchHit>(query.K);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        do
        {
            if (reader.FieldCount != 7)
            {
                continue;
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDouble(6)));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        return hits;
    }

    public async Task<long> CountChunksAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM rag.rag_chunk");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Exact search switches index scans off for the statement. That setting is load-bearing: a tie-breaker in
    /// <c>ORDER BY</c> does not keep the HNSW index out, because the planner happily uses the index as the presorted key
    /// of an Incremental Sort, and the "exact" query then returns approximate neighbours (shown by an integration test).
    /// pgvector has no <c>FETCH EXACT</c>; exactness is a planner setting. Approximate search orders by the distance
    /// expression alone, and the inner LIMIT keeps the join out of the index scan.
    /// </summary>
    internal static string SearchSql(SearchMode mode)
    {
        var order = mode == SearchMode.Exact ? "dist, c.chunk_id" : "c.embedding <=> $1";
        return $"""
            SELECT t.chunk_id, t.doc_id, t.ord, t.heading, t.body, d.title, t.dist
            FROM (
                SELECT c.chunk_id, c.doc_id, c.ord, c.heading, c.body, c.embedding <=> $1 AS dist
                FROM rag.rag_chunk c
                ORDER BY {order}
                LIMIT $2
            ) t
            JOIN rag.rag_document d ON d.doc_id = t.doc_id
            ORDER BY t.dist, t.chunk_id
            """;
    }

    internal static IEnumerable<(string Name, string Value)> Settings(SearchQuery query)
    {
        if (query.Mode == SearchMode.Exact)
        {
            yield return ("enable_indexscan", "off");
            yield break;
        }

        if (query.EfSearch is { } ef)
        {
            yield return ("hnsw.ef_search", ef.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Probes is { } probes)
        {
            yield return ("ivfflat.probes", probes.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal static DocumentView ParseDocument(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var chunks = root.GetProperty("chunks").EnumerateArray()
            .Select(c => new ChunkView(c.GetProperty("chunkId").GetString()!, c.GetProperty("ord").GetInt32(), c.GetProperty("heading").GetString()!, c.GetProperty("body").GetString()!))
            .ToList();
        return new DocumentView(
            root.GetProperty("_id").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("source").GetString()!,
            root.GetProperty("_metadata").GetProperty("etag").GetString()!,
            chunks,
            json);
    }
}
