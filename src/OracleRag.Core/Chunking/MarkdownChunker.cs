using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OracleRag.Chunking;

/// <summary>A document as a client submits it: an id, a title, a source label and Markdown text.</summary>
public sealed record SourceDocument(string Id, string Title, string Source, string Markdown);

/// <summary>One retrievable unit of a document.</summary>
/// <param name="Id">Stable id: <c>{documentId}#{ord:D4}</c>.</param>
/// <param name="Ord">Position in the document, from 0.</param>
/// <param name="Heading">The <c>##</c> section the chunk came from.</param>
/// <param name="Text">The chunk text shown to the user and cited.</param>
/// <param name="EmbeddingText">What is embedded: <c>Title &gt; Heading</c> followed by the text.</param>
/// <param name="ContentHash">SHA-256 of <see cref="EmbeddingText"/>, hex.</param>
public sealed record Chunk(string Id, int Ord, string Heading, string Text, string EmbeddingText, string ContentHash);

/// <summary>
/// Structure-aware chunker: every <c>##</c> section starts a new chunk, and long sections are packed sentence by
/// sentence up to <see cref="MaxChars"/>. Each chunk is embedded with a contextual header (document title and
/// section heading), because in a corpus of near-duplicate documents the body alone does not say which entity a
/// sentence is about.
/// </summary>
public sealed partial class MarkdownChunker(int maxChars = 700)
{
    /// <summary>Headings are stored in a 400-byte column; 100 characters fit even at 4 bytes per character.</summary>
    public const int MaxHeadingChars = 100;

    /// <summary>Upper bound on a chunk's text length; longer sentences are wrapped at word boundaries.</summary>
    public int MaxChars { get; } = maxChars is > 50 and <= 1000 ? maxChars : throw new ArgumentOutOfRangeException(nameof(maxChars));

    public IReadOnlyList<Chunk> Split(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var chunks = new List<Chunk>();
        foreach (var (heading, body) in Sections(document.Markdown))
        {
            var buffer = new StringBuilder();
            foreach (var sentence in Sentences(body).SelectMany(HardWrap))
            {
                if (buffer.Length > 0 && buffer.Length + 1 + sentence.Length > MaxChars)
                {
                    Emit(document, heading, buffer, chunks);
                }

                if (buffer.Length > 0)
                {
                    buffer.Append(' ');
                }

                buffer.Append(sentence);
            }

            Emit(document, heading, buffer, chunks);
        }

        return chunks;
    }

    /// <summary>Splits text into sentences without breaking on decimals ("2.5") or common abbreviations.</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        var flat = Whitespace().Replace(text, " ").Trim();
        if (flat.Length == 0)
        {
            yield break;
        }

        foreach (var part in SentenceBoundary().Split(flat))
        {
            var sentence = part.Trim();
            if (sentence.Length > 0)
            {
                yield return sentence;
            }
        }
    }

    /// <summary>
    /// Splits a "sentence" longer than <see cref="MaxChars"/> (a table row, a run-on list) at word boundaries, so every
    /// chunk fits the 4,000-byte <c>VARCHAR2</c> body column even with multi-byte Turkish letters (4 x 700 &lt; 4,000).
    /// </summary>
    private IEnumerable<string> HardWrap(string sentence)
    {
        if (sentence.Length <= MaxChars)
        {
            yield return sentence;
            yield break;
        }

        var piece = new StringBuilder();
        foreach (var word in sentence.Split(' '))
        {
            var w = word.Length > MaxChars ? word[..MaxChars] : word;
            if (piece.Length > 0 && piece.Length + 1 + w.Length > MaxChars)
            {
                yield return piece.ToString();
                piece.Clear();
            }

            if (piece.Length > 0)
            {
                piece.Append(' ');
            }

            piece.Append(w);
        }

        if (piece.Length > 0)
        {
            yield return piece.ToString();
        }
    }

    private static void Emit(SourceDocument document, string heading, StringBuilder buffer, List<Chunk> chunks)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var text = buffer.ToString();
        buffer.Clear();
        var ord = chunks.Count;
        var embeddingText = $"{document.Title} > {heading}\n{text}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(embeddingText)));
        chunks.Add(new Chunk($"{document.Id}#{ord:D4}", ord, heading, text, embeddingText, hash));
    }

    private static IEnumerable<(string Heading, string Body)> Sections(string markdown)
    {
        var heading = "Overview";
        var body = new StringBuilder();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (body.Length > 0)
                {
                    yield return (heading, body.ToString());
                    body.Clear();
                }

                heading = line[3..].Trim();
                heading = heading.Length > MaxHeadingChars ? heading[..MaxHeadingChars] : heading;
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                // The document title is carried by the contextual header, not repeated as body text.
                continue;
            }
            else if (line.Length > 0)
            {
                body.Append(line.TrimStart('-', '*', ' ')).Append(' ');
            }
        }

        if (body.Length > 0)
        {
            yield return (heading, body.ToString());
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // End of sentence: . ! ? followed by whitespace and an upper-case letter or a digit, unless the period ends a
    // short abbreviation such as "Dr." or "No.".
    [GeneratedRegex(@"(?<!\b(?:Dr|Mr|Mrs|Ms|No|St|Prof|approx|e\.g|i\.e))(?<=[.!?])\s+(?=[A-ZİŞĞÜÖÇ0-9])")]
    private static partial Regex SentenceBoundary();
}
