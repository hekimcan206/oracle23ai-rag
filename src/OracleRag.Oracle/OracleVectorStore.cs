using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using OracleRag.Retrieval;

namespace OracleRag.Oracle;

/// <summary>
/// Oracle AI Vector Search store. Documents are written and read as JSON through the <c>rag_document_dv</c> duality
/// view; embeddings are set on the chunk rows in the same transaction; search runs <c>VECTOR_DISTANCE</c> with
/// <c>FETCH EXACT</c> or <c>FETCH APPROX</c>.
/// </summary>
public sealed class OracleVectorStore : IVectorStore
{
    private const int UniqueConstraintViolated = 1;
    private const int EtagMismatch = 42699;
    private const int ConcurrentModification = 42698;
    private const int MaxAttempts = 8;

    private readonly string _connectionString;
    private readonly string _owner;
    private readonly int _dimensions;
    private long _concurrentRetries;

    /// <param name="connectionString">Usually the least-privileged application account.</param>
    /// <param name="schemaOwner">Owner of the tables; validated against an identifier allow-list.</param>
    /// <param name="dimensions">Embedding dimensions the schema was installed with.</param>
    public OracleVectorStore(string connectionString, string schemaOwner, int dimensions)
    {
        _connectionString = connectionString;
        _owner = SqlIdentifier.Validate(schemaOwner).ToUpperInvariant();
        _dimensions = dimensions;
    }

    public string Name => "oracle";

    public async Task<WriteResult> UpsertAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string? expectedEtag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        ValidateEmbeddings(chunks);
        var json = DocumentJson(document, chunks, expectedEtag);

        // Duality-view updates are lock-free and optimistic: two sessions replacing the same document at the same
        // time make one of them fail with ORA-42698 ("concurrent modification detected") instead of waiting on a row
        // lock. A conditional write reports that as a conflict; an unconditional write ("last writer wins") retries
        // the whole transaction a bounded number of times with jitter (measured and discussed in ADR 0002).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WriteOnceAsync(document, chunks, json, expectedEtag, cancellationToken);
            }
            catch (OracleException ex) when (ex.Number == EtagMismatch || (ex.Number == ConcurrentModification && expectedEtag is not null))
            {
                throw new EtagMismatchException(document.Id);
            }
            catch (OracleException ex) when (ex.Number == ConcurrentModification && attempt < MaxAttempts)
            {
                Interlocked.Increment(ref _concurrentRetries);
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Unconditional writes that had to be retried after ORA-42698, since this instance was created.</summary>
    public long ConcurrentRetries => Interlocked.Read(ref _concurrentRetries);

    private async Task<WriteResult> WriteOnceAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string json, string? expectedEtag, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var created = await WriteDocumentAsync(connection, document.Id, json, expectedEtag, cancellationToken);
        await SetEmbeddingsAsync(connection, chunks, cancellationToken);
        var etag = await ReadEtagAsync(connection, document.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Document {document.Id} vanished inside its own transaction.");
        await transaction.CommitAsync(cancellationToken);
        return new WriteResult(etag, created);
    }

    public async Task<DocumentView?> GetAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, $"""
            SELECT JSON_SERIALIZE(v.data RETURNING CLOB)
            FROM {_owner}.rag_document_dv v
            WHERE v.data."_id" = :id
            """);
        command.Parameters.Add("id", OracleDbType.Varchar2, documentId, ParameterDirection.Input);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : ParseDocument(json);
    }

    public async Task<bool> DeleteAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, $"""DELETE FROM {_owner}.rag_document_dv v WHERE v.data."_id" = :id""");
        command.Parameters.Add("id", OracleDbType.Varchar2, documentId, ParameterDirection.Input);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(_dimensions);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, SearchSql(_owner, query));
        command.Parameters.Add("q", OracleDbType.Vector_Float32, query.Vector, ParameterDirection.Input);
        if (query.Mode == SearchMode.Exact)
        {
            command.Parameters.Add("k", OracleDbType.Int32, query.K, ParameterDirection.Input);
        }

        var hits = new List<SearchHit>(query.K);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDouble(6)));
        }

        return hits;
    }

    public async Task<long> CountChunksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, $"SELECT COUNT(*) FROM {_owner}.rag_chunk");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The search statement. Exact search binds K; approximate search must format K and the index knobs as literals
    /// because <c>FETCH APPROX ... PARAMETERS (...)</c> does not accept bind variables. They are integers that passed
    /// <see cref="SearchQuery.Validate"/>, so no text from the caller reaches the SQL.
    /// </summary>
    /// <remarks>
    /// The top-K is computed on the chunk table alone and joined to the document afterwards, so the optimizer sees a
    /// plain single-table vector query it can answer from the index (plan checked in the integration tests).
    /// </remarks>
    internal static string SearchSql(string owner, SearchQuery query)
    {
        string fetch;
        // A secondary sort key would make the vector index unusable for FETCH APPROX; exact search breaks ties by id.
        var order = query.Mode == SearchMode.Exact ? "dist, c.chunk_id" : "dist";
        if (query.Mode == SearchMode.Exact)
        {
            fetch = "FETCH EXACT FIRST :k ROWS ONLY";
        }
        else
        {
            var k = query.K.ToString(CultureInfo.InvariantCulture);
            var knobs = new List<string>();
            if (query.EfSearch is { } ef)
            {
                knobs.Add($"EFSEARCH {ef.ToString(CultureInfo.InvariantCulture)}");
            }

            if (query.Probes is { } probes)
            {
                knobs.Add($"NEIGHBOR PARTITION PROBES {probes.ToString(CultureInfo.InvariantCulture)}");
            }

            fetch = knobs.Count == 0
                ? $"FETCH APPROX FIRST {k} ROWS ONLY"
                : $"FETCH APPROX FIRST {k} ROWS ONLY WITH TARGET ACCURACY PARAMETERS ({string.Join(", ", knobs)})";
        }

        return $"""
            SELECT t.chunk_id, t.doc_id, t.ord, t.heading, t.body, d.title, t.dist
            FROM (
                SELECT c.chunk_id, c.doc_id, c.ord, c.heading, c.body, VECTOR_DISTANCE(c.embedding, :q, COSINE) AS dist
                FROM {owner}.rag_chunk c
                ORDER BY {order}
                {fetch}
            ) t
            JOIN {owner}.rag_document d ON d.doc_id = t.doc_id
            ORDER BY t.dist, t.chunk_id
            """;
    }

    /// <summary>The JSON document the duality view receives. With an expected tag it carries <c>_metadata.etag</c>,
    /// which makes Oracle reject the write (ORA-42699) when the stored document has changed since it was read.</summary>
    internal static string DocumentJson(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string? expectedEtag)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("_id", document.Id);
            if (expectedEtag is not null)
            {
                writer.WriteStartObject("_metadata");
                writer.WriteString("etag", expectedEtag);
                writer.WriteEndObject();
            }

            writer.WriteString("title", document.Title);
            writer.WriteString("source", document.Source);
            writer.WriteStartArray("chunks");
            foreach (var chunk in chunks)
            {
                writer.WriteStartObject();
                writer.WriteString("chunkId", chunk.Id);
                writer.WriteNumber("ord", chunk.Ord);
                writer.WriteString("heading", chunk.Heading);
                writer.WriteString("body", chunk.Text);
                writer.WriteString("contentHash", chunk.ContentHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static DocumentView ParseDocument(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var chunks = root.GetProperty("chunks").EnumerateArray()
            .Select(c => new ChunkView(
                c.GetProperty("chunkId").GetString()!,
                c.GetProperty("ord").GetInt32(),
                c.GetProperty("heading").GetString()!,
                c.GetProperty("body").GetString()!))
            .OrderBy(c => c.Ord)
            .ToList();
        return new DocumentView(
            root.GetProperty("_id").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("source").GetString()!,
            root.GetProperty("_metadata").GetProperty("etag").GetString()!,
            chunks,
            json);
    }

    private async Task<bool> WriteDocumentAsync(OracleConnection connection, string id, string json, string? expectedEtag, CancellationToken cancellationToken)
    {
        if (await UpdateDocumentAsync(connection, id, json, cancellationToken))
        {
            return false;
        }

        if (expectedEtag is not null)
        {
            // A conditional write against a document that no longer exists is a conflict, not a create.
            throw new EtagMismatchException(id);
        }

        try
        {
            await using var insert = Command(connection, $"INSERT INTO {_owner}.rag_document_dv VALUES (:doc)");
            insert.Parameters.Add("doc", OracleDbType.Clob, json, ParameterDirection.Input);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (OracleException ex) when (ex.Number == UniqueConstraintViolated)
        {
            // Lost a create race with another writer: the document exists now, so this write becomes a replace.
            return await UpdateDocumentAsync(connection, id, json, cancellationToken)
                ? false
                : throw new InvalidOperationException($"Document {id} could be neither inserted nor updated.", ex);
        }
    }

    private async Task<bool> UpdateDocumentAsync(OracleConnection connection, string id, string json, CancellationToken cancellationToken)
    {
        await using var update = Command(connection, $"""UPDATE {_owner}.rag_document_dv v SET v.data = :doc WHERE v.data."_id" = :id""");
        update.Parameters.Add("doc", OracleDbType.Clob, json, ParameterDirection.Input);
        update.Parameters.Add("id", OracleDbType.Varchar2, id, ParameterDirection.Input);
        return await update.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>One array-bound round trip sets every chunk's vector.</summary>
    private async Task SetEmbeddingsAsync(OracleConnection connection, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        await using var command = Command(connection, $"UPDATE {_owner}.rag_chunk SET embedding = :emb WHERE chunk_id = :id");
        command.ArrayBindCount = chunks.Count;
        command.Parameters.Add("emb", OracleDbType.Vector_Float32, chunks.Select(c => new OracleVector(c.Embedding)).ToArray(), ParameterDirection.Input);
        command.Parameters.Add("id", OracleDbType.Varchar2, chunks.Select(c => c.Id).ToArray(), ParameterDirection.Input);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != chunks.Count)
        {
            throw new InvalidOperationException($"Expected to set {chunks.Count} embeddings, set {updated}.");
        }
    }

    private async Task<string?> ReadEtagAsync(OracleConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, $"""
            SELECT JSON_VALUE(v.data, '$._metadata.etag')
            FROM {_owner}.rag_document_dv v
            WHERE v.data."_id" = :id
            """);
        command.Parameters.Add("id", OracleDbType.Varchar2, id, ParameterDirection.Input);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private void ValidateEmbeddings(IReadOnlyList<ChunkRecord> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.Length != _dimensions)
            {
                throw new ArgumentException($"Chunk {chunk.Id} has {chunk.Embedding.Length} dimensions; the schema expects {_dimensions}.");
            }
        }
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static OracleCommand Command(OracleConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql;
        return command;
    }
}
