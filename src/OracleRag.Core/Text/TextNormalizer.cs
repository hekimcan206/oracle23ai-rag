using System.Text;

namespace OracleRag.Text;

/// <summary>
/// Culture-independent tokenisation shared by the embedder and the answer extractor.
/// </summary>
/// <remarks>
/// The API runs with invariant globalisation (the chiseled image has no ICU), so <c>ToLower(tr-TR)</c> is not available.
/// Turkish letters are folded to ASCII instead: <c>İzmir</c>, <c>IZMIR</c>, <c>izmir</c> and <c>Izmir</c> become the
/// same token, which is what a user typing without a Turkish keyboard expects. Numbers keep their digits and drop
/// thousands separators, so <c>2.450</c> and <c>2,450</c> are the same token.
/// </remarks>
public static class TextNormalizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "if", "in",
        "is", "it", "its", "much", "my", "of", "on", "or", "the", "their", "there", "this", "to", "what", "when",
        "where", "which", "who", "will", "with", "you", "your", "should", "must", "after", "before", "per",
    };

    /// <summary>Folds one character: Turkish letters to ASCII, everything else to invariant lower case.</summary>
    public static char Fold(char c) => c switch
    {
        'ı' or 'İ' or 'I' => 'i',
        'ş' or 'Ş' => 's',
        'ğ' or 'Ğ' => 'g',
        'ü' or 'Ü' => 'u',
        'ö' or 'Ö' => 'o',
        'ç' or 'Ç' => 'c',
        'â' or 'Â' => 'a',
        'î' or 'Î' => 'i',
        'û' or 'Û' => 'u',
        _ => char.ToLowerInvariant(c),
    };

    /// <summary>Splits text into folded tokens. Stop words are kept; use <see cref="ContentTokens"/> to drop them.</summary>
    public static List<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                current.Append(Fold(c));
                continue;
            }

            // A '.' or ',' between two digits is a thousands or decimal separator, not a token boundary.
            if ((c == '.' || c == ',') && current.Length > 0 && char.IsDigit(text[i - 1])
                && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        return tokens;
    }

    /// <summary>
    /// Tokens without stop words, with a light plural fold (<c>hours</c> → <c>hour</c>): the features that carry
    /// meaning. The fold is deliberately crude; it only has to make a question's "visitor" meet a document's "visitors".
    /// </summary>
    public static List<string> ContentTokens(string text) =>
        Tokens(text).Where(t => !StopWords.Contains(t)).Select(FoldPlural).ToList();

    internal static string FoldPlural(string token) =>
        token.Length > 3 && token[^1] == 's' && token[^2] != 's' && !char.IsDigit(token[^2]) ? token[..^1] : token;

    private static void Flush(StringBuilder current, List<string> tokens)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
            current.Clear();
        }
    }
}
