using FsCheck;
using FsCheck.Xunit;
using OracleRag.Evaluation;
using OracleRag.Retrieval;

namespace OracleRag.UnitTests.Retrieval;

public sealed class InMemoryVectorStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChunkRecord Chunk(string doc, int ord, params float[] v) => new($"{doc}#{ord:D4}", ord, "H", $"text {ord}", $"hash{ord}", v);

    [Fact]
    public async Task Replace_swaps_all_chunks_and_conditional_writes_detect_conflicts()
    {
        var store = new InMemoryVectorStore(2);
        var doc = new DocumentRecord("d1", "T", "s");
        var first = await store.UpsertAsync(doc, [Chunk("d1", 0, 1, 0), Chunk("d1", 1, 0, 1)], null, Ct);
        first.Created.Should().BeTrue();

        var second = await store.UpsertAsync(doc, [Chunk("d1", 0, 1, 1)], first.Etag, Ct);
        second.Created.Should().BeFalse();
        second.Etag.Should().NotBe(first.Etag);
        (await store.CountChunksAsync(Ct)).Should().Be(1);

        await FluentActions.Awaiting(() => store.UpsertAsync(doc, [Chunk("d1", 0, 0, 1)], first.Etag, Ct))
            .Should().ThrowAsync<EtagMismatchException>();
        await FluentActions.Awaiting(() => store.UpsertAsync(new DocumentRecord("missing", "T", "s"), [], "X", Ct))
            .Should().ThrowAsync<EtagMismatchException>("a conditional write cannot create");
    }

    [Fact]
    public async Task Search_orders_by_cosine_distance_then_chunk_id()
    {
        var store = new InMemoryVectorStore(2);
        await store.UpsertAsync(new DocumentRecord("b", "T", "s"), [Chunk("b", 0, 1, 0)], null, Ct);
        await store.UpsertAsync(new DocumentRecord("a", "T", "s"), [Chunk("a", 0, 1, 0), Chunk("a", 1, 0, 1)], null, Ct);

        var hits = await store.SearchAsync(new SearchQuery([1, 0], 3), Ct);

        hits.Select(h => h.ChunkId).Should().Equal("a#0000", "b#0000", "a#0001");
        hits[0].Distance.Should().BeApproximately(0, 1e-9);
        hits[2].Distance.Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public async Task Get_renders_the_same_json_shape_as_the_databases_and_delete_removes_it()
    {
        var store = new InMemoryVectorStore(2);
        await store.UpsertAsync(new DocumentRecord("d", "Title", "src"), [Chunk("d", 0, 1, 0)], null, Ct);

        var view = await store.GetAsync("d", Ct);
        view!.Json.Should().Contain("\"_id\":\"d\"").And.Contain("\"_metadata\":{\"etag\":").And.Contain("\"chunkId\":\"d#0000\"");
        (await store.DeleteAsync("d", Ct)).Should().BeTrue();
        (await store.GetAsync("d", Ct)).Should().BeNull();
        (await store.DeleteAsync("d", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Wrong_dimensions_are_rejected_on_write_and_on_search()
    {
        var store = new InMemoryVectorStore(3);
        await FluentActions.Awaiting(() => store.UpsertAsync(new DocumentRecord("d", "T", "s"), [Chunk("d", 0, 1, 0)], null, Ct))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => store.SearchAsync(new SearchQuery([1, 0], 1), Ct)).Should().ThrowAsync<ArgumentException>();
    }
}

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData(0, null, null)]
    [InlineData(101, null, null)]
    [InlineData(10, 5, null)]
    [InlineData(10, 1001, null)]
    [InlineData(10, null, 0)]
    [InlineData(10, null, 1001)]
    public void Out_of_range_knobs_are_rejected_before_they_reach_sql(int k, int? ef, int? probes) =>
        FluentActions.Invoking(() => new SearchQuery(new float[4], k, SearchMode.Approximate, ef, probes).Validate(4))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Valid_query_passes() =>
        FluentActions.Invoking(() => new SearchQuery(new float[4], 10, SearchMode.Approximate, 40, 8).Validate(4)).Should().NotThrow();
}

public sealed class ContentEtagTests
{
    private static readonly DocumentRecord Doc = new("d", "Title", "s");

    [Property]
    public bool Same_content_same_tag_any_change_new_tag(NonEmptyString text, NonEmptyString other)
    {
        ChunkRecord C(string t) => new("d#0000", 0, "H", t, "h", [1f]);
        var a = ContentEtag.Compute(Doc, [C(text.Get)]);
        var b = ContentEtag.Compute(Doc, [C(text.Get)]);
        var c = ContentEtag.Compute(Doc, [C(text.Get + other.Get)]);
        return a == b && a != c && a.Length == 32;
    }

    [Fact]
    public void Embeddings_do_not_change_the_tag()
    {
        ChunkRecord C(float x) => new("d#0000", 0, "H", "t", "h", [x]);
        ContentEtag.Compute(Doc, [C(1)]).Should().Be(ContentEtag.Compute(Doc, [C(2)]));
    }
}

public sealed class VectorMathAndMetricsTests
{
    [Property]
    public bool Cosine_distance_is_symmetric_bounded_and_zero_on_itself(NonEmptyArray<NormalFloat> values)
    {
        var a = values.Get.Select(v => (float)v.Get).ToArray();
        var b = a.Reverse().ToArray();
        var ab = VectorMath.CosineDistance(a, b);
        var self = VectorMath.CosineDistance(a, a);
        var zero = a.All(x => x == 0);
        return Math.Abs(ab - VectorMath.CosineDistance(b, a)) < 1e-12 && ab >= -1e-9 && ab <= 2 + 1e-9 && (zero || Math.Abs(self) < 1e-6);
    }

    [Fact]
    public void Hit_and_reciprocal_rank()
    {
        string[] ranked = ["a", "b", "c"];
        Metrics.HitAtK(ranked, "b", 1).Should().Be(0);
        Metrics.HitAtK(ranked, "b", 2).Should().Be(1);
        Metrics.ReciprocalRank(ranked, "c").Should().BeApproximately(1 / 3.0, 1e-12);
        Metrics.ReciprocalRank(ranked, "z").Should().Be(0);
    }

    [Fact]
    public void Overlap_recall_ignores_order()
    {
        Metrics.OverlapRecall(["c", "b", "a"], ["a", "b", "c"], 3).Should().Be(1);
        Metrics.OverlapRecall(["a", "x", "y", "b"], ["a", "b", "c", "d"], 4).Should().Be(0.5);
    }

    [Fact]
    public void Percentiles_use_nearest_rank_and_median_averages_the_middle_pair()
    {
        double[] sample = [5, 1, 4, 2, 3, 10, 9, 8, 7, 6];
        Metrics.Percentile(sample, 50).Should().Be(5);
        Metrics.Percentile(sample, 95).Should().Be(10);
        Metrics.Percentile(sample, 0).Should().Be(1);
        Metrics.Median(sample).Should().Be(5.5);
        Metrics.Median([3, 1, 2]).Should().Be(2);
    }

    [Property]
    public bool Percentile_is_monotonic_in_p(NonEmptyArray<NormalFloat> values, byte p1, byte p2)
    {
        var sample = values.Get.Select(v => v.Get).ToArray();
        var (lo, hi) = (Math.Min(p1, p2) % 101, Math.Max(p1, p2) % 101);
        return lo > hi || Metrics.Percentile(sample, lo) <= Metrics.Percentile(sample, hi);
    }
}
