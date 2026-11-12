using System.Collections.Concurrent;
using System.Text;
using OracleRag.Text;

namespace OracleRag.Embeddings;

/// <summary>Turns text into a fixed-length, L2-normalised vector.</summary>
public interface IEmbedder
{
    /// <summary>Identifies the model and its version. Stored next to the vectors so a store never mixes models.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    float[] Embed(string text);
}

/// <summary>
/// A deterministic, download-free embedder: every content token and adjacent token pair is mapped to a fixed
/// pseudo-random ±1 vector (seeded by a hash of the feature), and a text's embedding is the normalised, weighted sum
/// of its feature vectors (random indexing / sign random projection).
/// </summary>
/// <remarks>
/// <para>This is a <b>lexical</b> embedding, not a semantic one: two texts are close when they share words, and a
/// synonym counts for nothing. That is deliberate. The repository compares two vector stores on the same vectors, and
/// a deterministic embedder makes every number reproducible on any machine without a model download. The vectors are
/// dense, unit length and clustered by shared vocabulary, which is what HNSW and IVF indexes care about; see
/// ADR 0003 for what this does and does not say about results with a neural model.</para>
/// <para>Two random ±1 vectors of dimension d have cosine ≈ N(0, 1/d); at d = 384 unrelated features contribute
/// noise with a standard deviation of about 0.05.</para>
/// </remarks>
public sealed class LexicalProjectionEmbedder : IEmbedder
{
    private const float BigramWeight = 0.5f;
    private readonly ulong _seed;
    private readonly ConcurrentDictionary<string, sbyte[]> _features = new(StringComparer.Ordinal);

    public LexicalProjectionEmbedder(int dimensions = 384, ulong seed = 0x5EED_0A1C_2026)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4096);
        Dimensions = dimensions;
        _seed = seed;
        ModelId = $"lexproj-{dimensions}-v1-{seed:x}";
    }

    public string ModelId { get; }

    public int Dimensions { get; }

    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = TextNormalizer.ContentTokens(text);
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            weights[token] = weights.GetValueOrDefault(token) + 1f;
        }

        // Sub-linear term frequency: the fifth mention of a word adds less than the first.
        foreach (var key in weights.Keys.ToList())
        {
            weights[key] = 1f + MathF.Log(weights[key]);
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var bigram = string.Concat(tokens[i - 1], " ", tokens[i]);
            weights[bigram] = weights.GetValueOrDefault(bigram) + BigramWeight;
        }

        var vector = new float[Dimensions];
        foreach (var (feature, weight) in weights)
        {
            var signs = _features.GetOrAdd(feature, Signs);
            for (var d = 0; d < vector.Length; d++)
            {
                vector[d] += weight * signs[d];
            }
        }

        return Normalize(vector);
    }

    /// <summary>Scales a vector to unit length; the zero vector (empty text) is returned unchanged.</summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        if (sum == 0)
        {
            return vector;
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }

        return vector;
    }

    private sbyte[] Signs(string feature)
    {
        // FNV-1a over UTF-8 gives a stable 64-bit seed per feature (string.GetHashCode is randomised per process).
        var state = 14695981039346656037UL ^ _seed;
        foreach (var b in Encoding.UTF8.GetBytes(feature))
        {
            state = (state ^ b) * 1099511628211UL;
        }

        var signs = new sbyte[Dimensions];
        for (var d = 0; d < signs.Length; d += 64)
        {
            var bits = SplitMix64(ref state);
            for (var j = 0; j < 64 && d + j < signs.Length; j++)
            {
                signs[d + j] = (sbyte)(((bits >> j) & 1UL) == 0 ? -1 : 1);
            }
        }

        return signs;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
