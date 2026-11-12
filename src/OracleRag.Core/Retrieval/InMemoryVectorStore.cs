using System.Text.Json;

namespace OracleRag.Retrieval;

public static class VectorMath
{
    /// <summary>Cosine distance (1 − cosine similarity), computed in double like both databases report it.</summary>
    public static double CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors differ in length.");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 1.0 : 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}

/// <summary>
/// Brute-force reference store. It is the ground truth for recall measurements and the store the API unit tests use;
/// it is not meant for production data volumes.
/// </summary>
public sealed class InMemoryVectorStore(int dimensions) : IVectorStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _documents = new(StringComparer.Ordinal);

    public string Name => "memory";

    public int Dimensions { get; } = dimensions;

    public Task<WriteResult> UpsertAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, string? expectedEtag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.Length != Dimensions)
            {
                throw new ArgumentException($"Chunk {chunk.Id} has {chunk.Embedding.Length} dimensions; expected {Dimensions}.");
            }
        }

        lock (_gate)
        {
            var exists = _documents.TryGetValue(document.Id, out var current);
            if (expectedEtag is not null && (!exists || current!.Etag != expectedEtag))
            {
                throw new EtagMismatchException(document.Id);
            }

            var entry = new Entry(document, [.. chunks.OrderBy(c => c.Ord)], ContentEtag.Compute(document, chunks));
            _documents[document.Id] = entry;
            return Task.FromResult(new WriteResult(entry.Etag, !exists));
        }
    }

    public Task<DocumentView?> GetAsync(string documentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(documentId, out var entry))
            {
                return Task.FromResult<DocumentView?>(null);
            }

            var chunks = entry.Chunks.Select(c => new ChunkView(c.Id, c.Ord, c.Heading, c.Text)).ToList();
            var json = JsonSerializer.Serialize(new
            {
                _id = entry.Document.Id,
                title = entry.Document.Title,
                source = entry.Document.Source,
                _metadata = new { etag = entry.Etag },
                chunks = chunks.Select(c => new { chunkId = c.Id, ord = c.Ord, heading = c.Heading, body = c.Text }),
            });
            return Task.FromResult<DocumentView?>(new DocumentView(entry.Document.Id, entry.Document.Title, entry.Document.Source, entry.Etag, chunks, json));
        }
    }

    public Task<bool> DeleteAsync(string documentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_documents.Remove(documentId));
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(Dimensions);
        List<(ChunkRecord Chunk, DocumentRecord Document)> all;
        lock (_gate)
        {
            all = [.. _documents.Values.SelectMany(e => e.Chunks.Select(c => (c, e.Document)))];
        }

        // Approximate mode is answered exactly: the reference store has no index to be approximate with.
        IReadOnlyList<SearchHit> hits = all
            .Select(x => new SearchHit(x.Chunk.Id, x.Document.Id, x.Chunk.Ord, x.Chunk.Heading, x.Chunk.Text, x.Document.Title,
                VectorMath.CosineDistance(query.Vector, x.Chunk.Embedding)))
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.ChunkId, StringComparer.Ordinal)
            .Take(query.K)
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<long> CountChunksAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_documents.Values.Sum(e => e.Chunks.Count));
        }
    }

    private sealed record Entry(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks, string Etag);
}
