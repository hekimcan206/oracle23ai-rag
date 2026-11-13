using OracleRag.Answering;
using OracleRag.Chunking;
using OracleRag.Evaluation;
using OracleRag.Ingestion;
using OracleRag.Retrieval;
using OracleRag.Synthetic;

namespace OracleRag.IntegrationTests;

/// <summary>
/// One suite, two databases. Every test here runs against Oracle and against PostgreSQL, and most compare the database
/// with the brute-force reference store loaded with the same vectors: "same dataset, same answers" is asserted.
/// </summary>
public abstract class VectorStoreContract
{
    /// <summary>Distances computed in float32 by the databases and in double by the reference differ in the 7th digit.</summary>
    protected const double DistanceTolerance = 1e-5;

    protected static readonly RagService Rag = new(new MarkdownChunker(), OracleFixture.Embedder, new ExtractiveAnswerer());

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract IVectorStore Store { get; }

    protected abstract IVectorIndexAdmin Admin { get; }

    /// <summary>Empties both tables and drops the vector index (owner account).</summary>
    protected abstract Task ResetAsync();

    /// <summary>Loads documents through the owner's bulk path (array binding / binary COPY).</summary>
    protected abstract Task BulkLoadAsync(IReadOnlyList<(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks)> items);

    protected async Task<InMemoryVectorStore> LoadCorpusAsync(GeneratedCorpus corpus, bool bulk)
    {
        await ResetAsync();
        var items = corpus.Documents.Select(Rag.Prepare).ToList();
        var reference = new InMemoryVectorStore(OracleFixture.Embedder.Dimensions);
        foreach (var (document, chunks) in items)
        {
            await reference.UpsertAsync(document, chunks, null, Ct);
            if (!bulk)
            {
                await Store.UpsertAsync(document, chunks, null, Ct);
            }
        }

        if (bulk)
        {
            await BulkLoadAsync(items);
        }

        return reference;
    }

    [Fact]
    public async Task Replace_swaps_the_chunk_set_atomically_and_delete_removes_everything()
    {
        await ResetAsync();
        var source = new SourceDocument("contract-replace", "Testkent Clinic — Parking", "test", "## A\nFirst section.\n## B\nSecond section.\n## C\nThird.");
        var created = await Rag.IngestAsync(Store, source, null, Ct);
        created.Write.Created.Should().BeTrue();
        created.Chunks.Should().Be(3);

        var replaced = await Rag.IngestAsync(Store, source with { Markdown = "## Only\nOne section now." }, null, Ct);
        replaced.Write.Created.Should().BeFalse();
        var view = await Store.GetAsync("contract-replace", Ct);
        view!.Chunks.Select(c => c.Heading).Should().Equal("Only");
        view.Etag.Should().Be(replaced.Write.Etag);
        (await Store.CountChunksAsync(Ct)).Should().Be(1, "the chunks of the old version are gone");

        (await Store.DeleteAsync("contract-replace", Ct)).Should().BeTrue();
        (await Store.GetAsync("contract-replace", Ct)).Should().BeNull();
        (await Store.CountChunksAsync(Ct)).Should().Be(0);
        (await Store.DeleteAsync("contract-replace", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Etag_is_a_content_tag_and_conditional_writes_detect_lost_updates()
    {
        await ResetAsync();
        var source = new SourceDocument("contract-etag", "Testkent Clinic — Fees", "test", "## Exam\nAn exam costs 900 TL.");
        var v1 = (await Rag.IngestAsync(Store, source, null, Ct)).Write.Etag;
        (await Rag.IngestAsync(Store, source, null, Ct)).Write.Etag.Should().Be(v1, "rewriting identical content keeps the tag");

        // Writer A read v1; writer B changes the document; A's conditional write must fail instead of overwriting B.
        var v2 = (await Rag.IngestAsync(Store, source with { Markdown = "## Exam\nAn exam costs 950 TL." }, v1, Ct)).Write.Etag;
        v2.Should().NotBe(v1);
        await FluentActions.Awaiting(() => Rag.IngestAsync(Store, source with { Markdown = "## Exam\nAn exam costs 1.000 TL." }, v1, Ct))
            .Should().ThrowAsync<EtagMismatchException>();
        (await Store.GetAsync("contract-etag", Ct))!.Chunks.Single().Text.Should().Contain("950");

        await FluentActions.Awaiting(() => Rag.IngestAsync(Store, source with { Id = "contract-etag-missing" }, v1, Ct))
            .Should().ThrowAsync<EtagMismatchException>("a conditional write cannot create a document");
        (await Store.GetAsync("contract-etag-missing", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_writers_of_one_new_document_serialise_without_errors_or_orphans()
    {
        await ResetAsync();
        var versions = Enumerable.Range(0, 8)
            .Select(i => new SourceDocument("contract-race", "Racekent Clinic — Pharmacy", "test", $"## Hours\nOpen until {10 + i}:00.\n## Night\nVersion {i}."))
            .ToList();

        var results = await Task.WhenAll(versions.Select(v => Task.Run(() => Rag.IngestAsync(Store, v, null, Ct), Ct)));

        results.Count(r => r.Write.Created).Should().Be(1, "exactly one writer creates the document");
        var final = await Store.GetAsync("contract-race", Ct);
        final!.Chunks.Should().HaveCount(2);
        results.Select(r => r.Write.Etag).Should().Contain(final.Etag, "the stored document is one of the written versions, not a mix");
        (await Store.CountChunksAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Exact_search_returns_the_reference_ranking_and_distances()
    {
        var corpus = CorpusGenerator.Generate(15);
        var reference = await LoadCorpusAsync(corpus, bulk: false);

        foreach (var q in corpus.Questions.Take(40))
        {
            var vector = OracleFixture.Embedder.Embed(q.Question);
            var expected = await reference.SearchAsync(new SearchQuery(vector, 10), Ct);
            var actual = await Store.SearchAsync(new SearchQuery(vector, 10), Ct);
            AssertSameRanking(actual, expected);
        }
    }

    [Fact]
    public async Task Hnsw_search_reaches_high_recall_and_sees_rows_written_after_the_build()
    {
        var corpus = CorpusGenerator.Generate(250);
        var reference = await LoadCorpusAsync(corpus, bulk: true);
        await Admin.CreateIndexAsync(VectorIndexSpec.Hnsw(), Ct);
        (await Admin.IndexSizeBytesAsync(Ct)).Should().BeGreaterThan(0);

        var recall = new List<double>();
        foreach (var q in corpus.Questions.Take(100))
        {
            var vector = OracleFixture.Embedder.Embed(q.Question);
            var exact = await reference.SearchAsync(new SearchQuery(vector, 10), Ct);
            var approx = await Store.SearchAsync(new SearchQuery(vector, 10, SearchMode.Approximate, EfSearch: 100), Ct);
            approx.Should().HaveCount(10);
            recall.Add(Metrics.OverlapRecall([.. approx.Select(h => h.ChunkId)], [.. exact.Select(h => h.ChunkId)], 10));
        }

        recall.Average().Should().BeGreaterThan(0.9);

        // Freshness: a document written through the application path after the index exists is found by index search.
        var late = new SourceDocument("zz-late-document", "Latekent Clinic — Parking", "test", "## Car park\nThe Latekent car park charges 77 TL per hour.");
        await Rag.IngestAsync(Store, late, null, Ct);
        var exactLate = await Rag.SearchAsync(Store, "Latekent car park charges per hour", 10, SearchMode.Exact, null, null, Ct);
        var hits = await Rag.SearchAsync(Store, "Latekent car park charges per hour", 10, SearchMode.Approximate, 100, null, Ct);
        exactLate[0].DocumentId.Should().Be("zz-late-document");
        hits.Select(h => h.DocumentId).Should().Contain("zz-late-document", $"exact rank 1 distance {exactLate[0].Distance:F4}; approximate returned {string.Join(", ", hits.Select(h => $"{h.DocumentId}:{h.Distance:F4}"))}");
    }

    [Fact]
    public async Task Ivf_search_probing_every_partition_is_exact()
    {
        var corpus = CorpusGenerator.Generate(60);
        var reference = await LoadCorpusAsync(corpus, bulk: true);
        await Admin.CreateIndexAsync(VectorIndexSpec.Ivf(8), Ct);

        double recallAll = 0, recallOne = 0;
        var questions = corpus.Questions.Take(40).ToList();
        foreach (var q in questions)
        {
            var vector = OracleFixture.Embedder.Embed(q.Question);
            var exact = (await reference.SearchAsync(new SearchQuery(vector, 10), Ct)).Select(h => h.ChunkId).ToList();
            var all = await Store.SearchAsync(new SearchQuery(vector, 10, SearchMode.Approximate, Probes: 8), Ct);
            var one = await Store.SearchAsync(new SearchQuery(vector, 10, SearchMode.Approximate, Probes: 1), Ct);
            recallAll += Metrics.OverlapRecall([.. all.Select(h => h.ChunkId)], exact, 10);
            recallOne += Metrics.OverlapRecall([.. one.Select(h => h.ChunkId)], exact, 10);
        }

        (recallAll / questions.Count).Should().BeGreaterThan(0.99, "probing all partitions scans every vector");
        (recallOne / questions.Count).Should().BeLessThan(recallAll / questions.Count, "one probe out of eight must miss neighbours");
    }

    [Fact]
    public async Task Ask_returns_the_same_cited_answers_as_the_reference_store()
    {
        var corpus = CorpusGenerator.Generate(20);
        var reference = await LoadCorpusAsync(corpus, bulk: false);

        var agree = 0;
        foreach (var q in corpus.Questions)
        {
            var db = await Rag.AskAsync(Store, q.Question, 5, SearchMode.Exact, Ct);
            var mem = await Rag.AskAsync(reference, q.Question, 5, SearchMode.Exact, Ct);
            agree += db.Answer.Text == mem.Answer.Text && db.Answer.Citations.Select(c => c.ChunkId).SequenceEqual(mem.Answer.Citations.Select(c => c.ChunkId)) ? 1 : 0;
        }

        agree.Should().Be(corpus.Questions.Count);
    }

    [Fact]
    public async Task Wrong_dimensions_never_reach_the_database()
    {
        await FluentActions.Awaiting(() => Store.SearchAsync(new SearchQuery(new float[3], 5), Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Store.UpsertAsync(new DocumentRecord("d", "T", "s"), [new ChunkRecord("d#0000", 0, "H", "x", "h", new float[3])], null, Ct))
            .Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Same ids in the same order, except that ids whose distances tie within the tolerance may swap (including at the
    /// k-th position, where a tie decides which of two equally close chunks makes the cut).
    /// </summary>
    protected static void AssertSameRanking(IReadOnlyList<SearchHit> actual, IReadOnlyList<SearchHit> expected)
    {
        actual.Should().HaveSameCount(expected);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Distance.Should().BeApproximately(expected[i].Distance, DistanceTolerance);
            if (actual[i].ChunkId != expected[i].ChunkId)
            {
                Math.Abs(expected.First(e => e.ChunkId == actual[i].ChunkId || ReferenceEquals(e, expected[^1])).Distance - expected[i].Distance)
                    .Should().BeLessThan(DistanceTolerance, $"rank {i + 1} differs ({actual[i].ChunkId} vs {expected[i].ChunkId}) without a tie");
            }
        }
    }
}
