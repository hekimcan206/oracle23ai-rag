namespace OracleRag.Retrieval;

/// <summary>Document-level fields, as stored.</summary>
public sealed record DocumentRecord(string Id, string Title, string Source);

/// <summary>A chunk ready to store: text plus its embedding.</summary>
public sealed record ChunkRecord(string Id, int Ord, string Heading, string Text, string ContentHash, float[] Embedding);

/// <summary>
/// A stored document with its optimistic-concurrency tag. <c>Json</c> is the document exactly as the database renders
/// it (Oracle duality view or PostgreSQL jsonb).
/// </summary>
public sealed record DocumentView(string Id, string Title, string Source, string Etag, IReadOnlyList<ChunkView> Chunks, string Json);

public sealed record ChunkView(string Id, int Ord, string Heading, string Text);

/// <summary>Outcome of a write: the new tag and whether the document was created (not replaced).</summary>
public sealed record WriteResult(string Etag, bool Created);

public enum SearchMode
{
    /// <summary>Full scan, exact distances: the ground truth.</summary>
    Exact,

    /// <summary>Use the vector index (HNSW or IVF) with the given knobs.</summary>
    Approximate,
}

/// <summary>A nearest-neighbour query by cosine distance.</summary>
/// <param name="Vector">Query embedding (unit length).</param>
/// <param name="K">Rows to return, 1..100.</param>
/// <param name="Mode">Exact scan or index search.</param>
/// <param name="EfSearch">HNSW candidate-list size (pgvector <c>hnsw.ef_search</c>, Oracle <c>EFSEARCH</c>).</param>
/// <param name="Probes">IVF partitions to probe (pgvector <c>ivfflat.probes</c>, Oracle <c>NEIGHBOR PARTITION PROBES</c>).</param>
public sealed record SearchQuery(float[] Vector, int K, SearchMode Mode = SearchMode.Exact, int? EfSearch = null, int? Probes = null)
{
    public const int MaxK = 100;
    public const int MaxEfSearch = 1000;
    public const int MaxProbes = 1000;

    /// <summary>
    /// Validates the knobs. They end up as literals in Oracle's <c>FETCH APPROX ... PARAMETERS</c> clause, which does
    /// not take bind variables, so range checks here are part of the SQL-injection defence, not just hygiene.
    /// </summary>
    public void Validate(int dimensions)
    {
        ArgumentNullException.ThrowIfNull(Vector);
        if (Vector.Length != dimensions)
        {
            throw new ArgumentException($"Query vector has {Vector.Length} dimensions; the store expects {dimensions}.");
        }

        if (K is < 1 or > MaxK)
        {
            throw new ArgumentException($"K must be between 1 and {MaxK}.");
        }

        if (EfSearch is { } ef && (ef < K || ef > MaxEfSearch))
        {
            throw new ArgumentException($"EfSearch must be between K and {MaxEfSearch}.");
        }

        if (Probes is { } p && (p < 1 || p > MaxProbes))
        {
            throw new ArgumentException($"Probes must be between 1 and {MaxProbes}.");
        }
    }
}

public sealed record SearchHit(string ChunkId, string DocumentId, int Ord, string Heading, string Text, string Title, double Distance);

/// <summary>Raised when a conditional write carries a tag that no longer matches the stored document.</summary>
public sealed class EtagMismatchException(string documentId)
    : Exception($"Document '{documentId}' was changed by someone else; re-read it and retry.")
{
    public string DocumentId { get; } = documentId;
}

/// <summary>
/// The contract both databases implement. Integration tests run one shared suite against Oracle and PostgreSQL, so
/// "same dataset, same answers" is checked, not assumed.
/// </summary>
public interface IVectorStore
{
    /// <summary>Short name used in URLs, logs and reports: <c>oracle</c>, <c>postgres</c>, <c>memory</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Creates or replaces a document and all its chunks atomically. With <paramref name="expectedEtag"/> the write is
    /// conditional and throws <see cref="EtagMismatchException"/> when the stored tag differs or the document is gone.
    /// </summary>
    Task<WriteResult> UpsertAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string? expectedEtag, CancellationToken cancellationToken);

    Task<DocumentView?> GetAsync(string documentId, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    Task<long> CountChunksAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Content tag for stores that do not compute one themselves. Like Oracle's duality-view ETAG it is a hash of the
/// document's content, not a version counter: rewriting identical content keeps the tag, and any change produces a
/// new one. Embeddings are excluded on purpose (they are derived from the text).
/// </summary>
public static class ContentEtag
{
    public static string Compute(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        var canonical = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = document.Id,
            title = document.Title,
            source = document.Source,
            chunks = chunks.OrderBy(c => c.Ord).Select(c => new { c.Id, c.Ord, c.Heading, c.Text, c.ContentHash }),
        });
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 16);
    }
}
