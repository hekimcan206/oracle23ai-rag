using System.Text.RegularExpressions;
using OracleRag.Chunking;
using OracleRag.Retrieval;
using OracleRag.Text;

namespace OracleRag.Answering;

public sealed record Citation(int Number, string ChunkId, string DocumentId, string Title, string Heading, double Distance);

public sealed record Answer(string Text, bool Abstained, IReadOnlyList<Citation> Citations);

/// <summary>
/// Picks the sentence from the retrieved chunks that best covers the question and cites the chunk it came from, or
/// abstains with a fixed sentence. No language model: the answer is always a verbatim span of a stored document, so a
/// citation can never point at text the document does not contain.
/// </summary>
/// <remarks>
/// A sentence is scored by the share of question content tokens found in the sentence <i>or in its chunk's contextual
/// header</i> (document title and section heading). The header carries the entity ("Kuzeykent Clinic") and topic
/// ("Parking") that the sentence itself rarely repeats. Abstention has two gates: the best chunk must be close enough,
/// and the best sentence must cover enough of the question; the second gate is what refuses a question about a topic
/// the right entity's documents do not cover.
/// </remarks>
public sealed partial class ExtractiveAnswerer(double maxDistance = 0.75, double minCoverage = 0.6, int candidateChunks = 3)
{
    private const double AnswerTypeBonus = 0.2;

    public const string AbstentionText = "The documents do not answer this question.";

    public double MaxDistance { get; } = maxDistance;

    public double MinCoverage { get; } = minCoverage;

    public Answer Answer(string question, IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(hits);
        var questionTokens = TextNormalizer.ContentTokens(question).ToHashSet(StringComparer.Ordinal);
        if (questionTokens.Count == 0 || hits.Count == 0 || hits[0].Distance > MaxDistance)
        {
            return new Answer(AbstentionText, true, []);
        }

        var names = NameTokens(question);
        var expected = ExpectedAnswerType(question);
        if (expected == AnswerType.Time)
        {
            // "What time" asks for a clock time; the word itself is not evidence (a sentence about "the same time" is no answer).
            questionTokens.Remove("time");
        }

        (string Sentence, SearchHit Hit, double Score, double Coverage)? best = null;
        foreach (var (hit, rank) in hits.Take(candidateChunks).Select((h, i) => (h, i)))
        {
            var header = TextNormalizer.ContentTokens($"{hit.Title} {hit.Heading}");
            foreach (var sentence in MarkdownChunker.Sentences(hit.Text))
            {
                var covered = TextNormalizer.ContentTokens(sentence).Concat(header).ToHashSet(StringComparer.Ordinal);
                if (!names.All(covered.Contains))
                {
                    // Right topic, wrong entity: in a near-duplicate corpus this is the most dangerous answer of all.
                    continue;
                }

                var coverage = questionTokens.Count(covered.Contains) / (double)questionTokens.Count;
                // A tiny rank penalty breaks ties in favour of the chunk the retriever ranked higher.
                var score = coverage + (Matches(expected, sentence) ? AnswerTypeBonus : 0) - (0.001 * rank);
                if (best is null || score > best.Value.Score)
                {
                    best = (sentence, hit, score, coverage);
                }
            }
        }

        if (best is null || best.Value.Coverage < MinCoverage)
        {
            return new Answer(AbstentionText, true, []);
        }

        var h = best.Value.Hit;
        return new Answer($"{best.Value.Sentence} [1]", false, [new Citation(1, h.ChunkId, h.DocumentId, h.Title, h.Heading, h.Distance)]);
    }

    /// <summary>
    /// Tokens of capitalised words after the first word: a cheap stand-in for named-entity recognition. Every one of
    /// them must be covered by the cited chunk. A question typed in lower case has no name tokens and relies on the
    /// coverage gate alone (see Limitations in the README).
    /// </summary>
    internal enum AnswerType
    {
        Any,
        Time,
        Number,
    }

    /// <summary>
    /// Answer-type detection for the question shapes a patient-information desk gets ("what time", "how many", "how
    /// much", "what age"). A sentence containing the expected type gets a bonus worth more than one extra matched word.
    /// </summary>
    internal static AnswerType ExpectedAnswerType(string question)
    {
        if (TimeQuestion().IsMatch(question))
        {
            return AnswerType.Time;
        }

        return NumberQuestion().IsMatch(question) ? AnswerType.Number : AnswerType.Any;
    }

    private static bool Matches(AnswerType type, string sentence) => type switch
    {
        AnswerType.Time => ClockTime().IsMatch(sentence),
        AnswerType.Number => sentence.Any(char.IsDigit),
        _ => false,
    };

    [GeneratedRegex(@"\bwhat time\b|\buntil when\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeQuestion();

    [GeneratedRegex(@"\bhow (many|much|long)\b|\bwhat age\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberQuestion();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex ClockTime();

    internal static HashSet<string> NameTokens(string question)
    {
        var words = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Skip(1)
            .Where(w => char.IsUpper(w[0]))
            .SelectMany(TextNormalizer.ContentTokens)
            .ToHashSet(StringComparer.Ordinal);
    }
}
