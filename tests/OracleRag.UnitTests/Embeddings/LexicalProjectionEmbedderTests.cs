using FsCheck;
using FsCheck.Xunit;
using OracleRag.Embeddings;
using OracleRag.Retrieval;

namespace OracleRag.UnitTests.Embeddings;

public sealed class LexicalProjectionEmbedderTests
{
    private static readonly LexicalProjectionEmbedder Embedder = new();

    [Fact]
    public void Model_id_names_dimensions_version_and_seed()
    {
        Embedder.ModelId.Should().Be("lexproj-384-v1-5eed0a1c2026");
        Embedder.Dimensions.Should().Be(384);
    }

    [Fact]
    public void Vectors_are_pinned_across_processes_and_releases()
    {
        // string.GetHashCode is randomised per process; the embedder must not depend on it. These values are the first
        // components for a fixed text: if they change, every stored vector in every database is silently stale.
        var v = new LexicalProjectionEmbedder().Embed("Visiting hours at Kuzeykent Clinic");
        v.Take(4).Select(x => Math.Round((double)x, 4)).Should().Equal(PinnedPrefix);
    }

    private static readonly double[] PinnedPrefix = [-0.0814, 0.0349, 0.0349, -0.0116];

    [Fact]
    public void Empty_or_stop_word_only_text_is_the_zero_vector()
    {
        Embedder.Embed("").Should().OnlyContain(x => x == 0);
        Embedder.Embed("the of and").Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public void Different_seeds_give_unrelated_spaces()
    {
        var a = new LexicalProjectionEmbedder(seed: 1).Embed("parking fee");
        var b = new LexicalProjectionEmbedder(seed: 2).Embed("parking fee");
        Math.Abs(1 - VectorMath.CosineDistance(a, b)).Should().BeLessThan(0.3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5000)]
    public void Unsupported_dimensions_are_rejected(int dimensions) =>
        FluentActions.Invoking(() => new LexicalProjectionEmbedder(dimensions)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Turkish_spelling_variants_embed_identically()
    {
        Embedder.Embed("Işıklıova Belediyesi").Should().Equal(Embedder.Embed("isikliova belediyesi"));
    }

    [Property]
    public bool Embeddings_are_deterministic_and_unit_length(NonEmptyString text)
    {
        var a = Embedder.Embed(text.Get);
        var b = new LexicalProjectionEmbedder().Embed(text.Get);
        var norm = Math.Sqrt(a.Sum(x => (double)x * x));
        return a.SequenceEqual(b) && (norm == 0 || Math.Abs(norm - 1) < 1e-5);
    }

    [Property(MaxTest = 200)]
    public bool Sharing_words_brings_texts_closer_than_sharing_none(PositiveInt seed)
    {
        // Built from disjoint random vocabularies so the property does not depend on English.
        var random = new Random(seed.Get);
        string Word() => new([.. Enumerable.Range(0, 6).Select(_ => (char)('a' + random.Next(26)))]);
        var shared = Enumerable.Range(0, 4).Select(_ => Word()).ToList();
        var a = string.Join(' ', shared.Concat(Enumerable.Range(0, 2).Select(_ => Word())));
        var b = string.Join(' ', shared.Concat(Enumerable.Range(0, 2).Select(_ => Word())));
        var c = string.Join(' ', Enumerable.Range(0, 6).Select(_ => Word()));
        return VectorMath.CosineDistance(Embedder.Embed(a), Embedder.Embed(b)) < VectorMath.CosineDistance(Embedder.Embed(a), Embedder.Embed(c));
    }
}
