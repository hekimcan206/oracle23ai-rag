using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OracleRag.Chunking;

namespace OracleRag.UnitTests.Chunking;

public sealed class MarkdownChunkerTests
{
    private static readonly SourceDocument Sample = new(
        "kuzeykent-clinic-parking",
        "Kuzeykent Clinic — Parking",
        "test",
        """
        # Kuzeykent Clinic — Parking

        ## Car park
        The car park has 120 spaces. It charges 40 TL per hour.

        ## Accessible parking
        - There are 6 accessible spaces next to the north entrance.
        """);

    [Fact]
    public void Each_section_becomes_a_chunk_with_a_stable_id_and_a_contextual_header()
    {
        var chunks = new MarkdownChunker().Split(Sample);

        chunks.Select(c => c.Id).Should().Equal("kuzeykent-clinic-parking#0000", "kuzeykent-clinic-parking#0001");
        chunks[0].Heading.Should().Be("Car park");
        chunks[0].Text.Should().Be("The car park has 120 spaces. It charges 40 TL per hour.");
        chunks[0].EmbeddingText.Should().Be("Kuzeykent Clinic — Parking > Car park\nThe car park has 120 spaces. It charges 40 TL per hour.");
        chunks[1].Text.Should().Be("There are 6 accessible spaces next to the north entrance.", "list markers are not content");
    }

    [Fact]
    public void Content_hash_changes_with_the_text_and_with_the_header()
    {
        var chunker = new MarkdownChunker();
        var original = chunker.Split(Sample)[0].ContentHash;
        chunker.Split(Sample with { Markdown = Sample.Markdown.Replace("120", "121", StringComparison.Ordinal) })[0].ContentHash.Should().NotBe(original);
        chunker.Split(Sample with { Title = "Other Clinic — Parking" })[0].ContentHash.Should().NotBe(original);
        chunker.Split(Sample)[0].ContentHash.Should().Be(original);
    }

    [Fact]
    public void Text_before_the_first_heading_goes_into_an_overview_chunk()
    {
        var chunks = new MarkdownChunker().Split(new SourceDocument("d", "T", "s", "Intro sentence.\n## Details\nMore."));
        chunks.Select(c => c.Heading).Should().Equal("Overview", "Details");
    }

    [Theory]
    [InlineData("Dr. Ayşe Kaya sees patients. The fee is 2.5 times higher.", 2)]
    [InlineData("Ask at desk No. 3 please. Thanks!", 2)]
    [InlineData("Open 9:00. Closed on Sunday? Yes. 24 beds.", 4)]
    [InlineData("İstanbul office. İzmir office.", 2)]
    public void Sentence_splitter_respects_abbreviations_and_decimals(string text, int expected) =>
        MarkdownChunker.Sentences(text).Should().HaveCount(expected);

    [Fact]
    public void Long_sections_are_packed_sentence_by_sentence_and_run_on_text_is_wrapped()
    {
        var sentences = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"Sentence number {i} is here."));
        var runOn = string.Join(' ', Enumerable.Repeat("word", 400));
        var chunks = new MarkdownChunker(200).Split(new SourceDocument("d", "T", "s", $"## A\n{sentences}\n## B\n{runOn}"));

        chunks.Should().OnlyContain(c => c.Text.Length <= 200);
        chunks.Where(c => c.Heading == "A").Select(c => c.Text).Should().OnlyContain(t => t.EndsWith('.'), "sentences are never cut");
        chunks.Count(c => c.Heading == "B").Should().BeGreaterThan(1);
    }

    [Fact]
    public void Headings_are_capped_to_fit_the_heading_column()
    {
        var chunk = new MarkdownChunker().Split(new SourceDocument("d", "T", "s", $"## {new string('h', 300)}\nBody.")).Single();
        chunk.Heading.Should().HaveLength(MarkdownChunker.MaxHeadingChars);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(1001)]
    public void Chunk_size_outside_the_safe_range_is_rejected(int maxChars) =>
        FluentActions.Invoking(() => new MarkdownChunker(maxChars)).Should().Throw<ArgumentOutOfRangeException>();

    [Property(MaxTest = 200)]
    public Property Chunks_respect_the_size_limit_and_keep_every_word(NonEmptyArray<NonEmptyString> words, PositiveInt sectionEvery)
    {
        var tokens = words.Get.Select(w => new string(w.Get.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToList();
        var lines = tokens.Select((w, i) => i % (sectionEvery.Get % 7 + 1) == 0 ? $"## H{i}\n{w}." : $"{w}.");
        var chunks = new MarkdownChunker(120).Split(new SourceDocument("doc", "Title", "s", string.Join('\n', lines)));

        var withinLimit = chunks.All(c => c.Text.Length <= 120);
        var ordered = chunks.Select(c => c.Ord).SequenceEqual(Enumerable.Range(0, chunks.Count));
        var text = string.Join(' ', chunks.Select(c => c.Text));
        var allWords = tokens.All(t => text.Contains(t.Length > 120 ? t[..120] : t, StringComparison.Ordinal));
        return (withinLimit && ordered && allWords).Label($"chunks: {chunks.Count}");
    }
}
