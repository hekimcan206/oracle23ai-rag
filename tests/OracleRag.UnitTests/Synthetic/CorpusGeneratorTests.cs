using System.Text.Json;
using OracleRag.Answering;
using OracleRag.Chunking;
using OracleRag.Embeddings;
using OracleRag.Evaluation;
using OracleRag.Ingestion;
using OracleRag.Retrieval;
using OracleRag.Synthetic;
using OracleRag.UnitTests.Golden;

namespace OracleRag.UnitTests.Synthetic;

public sealed class CorpusGeneratorTests
{
    [Fact]
    public void Same_seed_same_corpus_and_a_larger_corpus_extends_a_smaller_one()
    {
        var small = CorpusGenerator.Generate(10);
        var again = CorpusGenerator.Generate(10);
        var large = CorpusGenerator.Generate(25);

        again.Documents.Should().BeEquivalentTo(small.Documents, o => o.WithStrictOrdering());
        large.Documents.Take(small.Documents.Count).Should().BeEquivalentTo(small.Documents, o => o.WithStrictOrdering(),
            "benchmarks at different scales must share their first documents");
        CorpusGenerator.Generate(10, seed: 7).Documents[0].Should().NotBe(small.Documents[0]);
    }

    [Fact]
    public void Every_entity_omits_exactly_one_topic_and_gets_one_unanswerable_question()
    {
        var corpus = CorpusGenerator.Generate(30);
        corpus.Documents.Should().HaveCount(30 * (CorpusGenerator.Topics.Length - 1));
        corpus.Questions.Count(q => !q.Answerable).Should().Be(30);
        corpus.Documents.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        corpus.Questions.Select(q => q.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_gold_chunk_exists_and_contains_the_gold_answer()
    {
        var corpus = CorpusGenerator.Generate(40);
        var chunker = new MarkdownChunker();
        var chunks = corpus.Documents.SelectMany(chunker.Split).ToDictionary(c => c.Id);

        foreach (var q in corpus.Questions.Where(q => q.Answerable))
        {
            chunks.Should().ContainKey(q.ExpectedChunkId!);
            chunks[q.ExpectedChunkId!].Text.Should().Contain(q.ExpectedAnswer!, q.Question);
        }
    }

    [Fact]
    public void Unanswerable_questions_really_have_no_document()
    {
        var corpus = CorpusGenerator.Generate(20);
        var ids = corpus.Documents.Select(d => d.Id).ToHashSet();
        foreach (var q in corpus.Questions.Where(q => !q.Answerable))
        {
            ids.Should().NotContain(q.Id.Replace("-unanswerable", "", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Işıkova Sahil Clinic", "isikova-sahil-clinic")]
    [InlineData("Çamköy İstasyon Eye Clinic", "camkoy-istasyon-eye-clinic")]
    public void Slugs_are_ascii(string name, string slug) => CorpusGenerator.Slug(name).Should().Be(slug);

    [Fact]
    public void Golden_corpus_sample_is_unchanged()
    {
        // The first three documents and their questions, committed as an approval file. A diff here means every
        // committed benchmark number was produced from a different corpus.
        var corpus = CorpusGenerator.Generate(1);
        var actual = JsonSerializer.Serialize(new { corpus.Documents, corpus.Questions }, GoldenFile.Options);
        GoldenFile.Verify("corpus-seed2026-1entity.json", actual);
    }
}

/// <summary>
/// Retrieval and answer quality on the reference store, as a regression gate. The thresholds sit a little below the
/// measured values (docs/benchmarks.md) so that a real regression fails the build and noise does not.
/// </summary>
public sealed class QualityGateTests
{
    [Fact]
    public async Task Exact_retrieval_and_extractive_answers_hold_their_measured_quality()
    {
        var corpus = CorpusGenerator.Generate(40);
        var rag = new RagService(new MarkdownChunker(), new LexicalProjectionEmbedder(), new ExtractiveAnswerer());
        var store = new InMemoryVectorStore(384);
        foreach (var document in corpus.Documents)
        {
            await rag.IngestAsync(store, document, null, TestContext.Current.CancellationToken);
        }

        double hits = 0, correct = 0, abstained = 0;
        var answerable = corpus.Questions.Where(q => q.Answerable).ToList();
        var unanswerable = corpus.Questions.Where(q => !q.Answerable).ToList();
        foreach (var q in answerable)
        {
            var result = await rag.AskAsync(store, q.Question, 5, SearchMode.Exact, TestContext.Current.CancellationToken);
            hits += Metrics.HitAtK([.. result.Hits.Select(h => h.ChunkId)], q.ExpectedChunkId!, 1);
            correct += !result.Answer.Abstained && result.Answer.Text.Contains(q.ExpectedAnswer!, StringComparison.Ordinal) && result.Answer.Citations[0].ChunkId == q.ExpectedChunkId ? 1 : 0;
        }

        foreach (var q in unanswerable)
        {
            abstained += (await rag.AskAsync(store, q.Question, 5, SearchMode.Exact, TestContext.Current.CancellationToken)).Answer.Abstained ? 1 : 0;
        }

        (hits / answerable.Count).Should().BeGreaterThanOrEqualTo(0.98);
        (correct / answerable.Count).Should().BeGreaterThanOrEqualTo(0.97);
        (abstained / unanswerable.Count).Should().BeGreaterThanOrEqualTo(0.9);
    }
}
