using System.Diagnostics;
using OracleRag.Answering;
using OracleRag.Chunking;
using OracleRag.Embeddings;
using OracleRag.Retrieval;

namespace OracleRag.Ingestion;

public sealed record AskResult(Answer Answer, IReadOnlyList<SearchHit> Hits, double RetrievalMs);

/// <summary>Chunk, embed and store on the way in; embed, search and answer on the way out.</summary>
public sealed class RagService(MarkdownChunker chunker, IEmbedder embedder, ExtractiveAnswerer answerer)
{
    public static readonly ActivitySource ActivitySource = new("OracleRag");

    public IEmbedder Embedder => embedder;

    /// <summary>Chunks and embeds a document. Pure: no I/O, so it is the same for every store.</summary>
    public (DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks) Prepare(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var chunks = chunker.Split(source)
            .Select(c => new ChunkRecord(c.Id, c.Ord, c.Heading, c.Text, c.ContentHash, embedder.Embed(c.EmbeddingText)))
            .ToList();
        return (new DocumentRecord(source.Id, source.Title, source.Source), chunks);
    }

    public async Task<WriteResult> IngestAsync(IVectorStore store, SourceDocument source, string? expectedEtag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var activity = ActivitySource.StartActivity("rag.ingest");
        activity?.SetTag("rag.store", store.Name);
        activity?.SetTag("rag.document_id", source.Id);
        var (document, chunks) = Prepare(source);
        activity?.SetTag("rag.chunks", chunks.Count);
        return await store.UpsertAsync(document, chunks, expectedEtag, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(IVectorStore store, string question, int k, SearchMode mode, int? efSearch, int? probes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var activity = ActivitySource.StartActivity("rag.search");
        activity?.SetTag("rag.store", store.Name);
        activity?.SetTag("rag.mode", mode.ToString());
        var query = new SearchQuery(embedder.Embed(question), k, mode, efSearch, probes);
        query.Validate(embedder.Dimensions);
        return await store.SearchAsync(query, cancellationToken);
    }

    public async Task<AskResult> AskAsync(IVectorStore store, string question, int k, SearchMode mode, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var hits = await SearchAsync(store, question, k, mode, null, null, cancellationToken);
        var retrievalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new AskResult(answerer.Answer(question, hits), hits, retrievalMs);
    }
}
