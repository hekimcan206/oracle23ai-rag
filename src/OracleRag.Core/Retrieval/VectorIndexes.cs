using System.Text.RegularExpressions;

namespace OracleRag.Retrieval;

public enum VectorIndexKind
{
    /// <summary>Hierarchical Navigable Small World graph (Oracle in-memory neighbour graph, pgvector <c>hnsw</c>).</summary>
    Hnsw,

    /// <summary>Inverted file / partitioned centroids (Oracle neighbour partitions, pgvector <c>ivfflat</c>).</summary>
    Ivf,
}

/// <summary>Build parameters, named the same for both databases so a benchmark can ask for identical indexes.</summary>
/// <param name="Neighbors">HNSW max connections per node (Oracle <c>NEIGHBORS</c>, pgvector <c>m</c>).</param>
/// <param name="EfConstruction">HNSW build candidate list (Oracle <c>EFCONSTRUCTION</c>, pgvector <c>ef_construction</c>).</param>
/// <param name="Partitions">IVF centroids (Oracle <c>NEIGHBOR PARTITIONS</c>, pgvector <c>lists</c>).</param>
public sealed record VectorIndexSpec(VectorIndexKind Kind, int Neighbors = 16, int EfConstruction = 64, int Partitions = 100)
{
    public static VectorIndexSpec Hnsw(int neighbors = 16, int efConstruction = 64) => new(VectorIndexKind.Hnsw, neighbors, efConstruction);

    public static VectorIndexSpec Ivf(int partitions) => new(VectorIndexKind.Ivf, Partitions: partitions);

    /// <summary>The values are formatted into DDL (neither database binds DDL parameters), so they are range-checked.</summary>
    public void Validate()
    {
        if (Neighbors is < 2 or > 100 || EfConstruction is < 4 or > 1000 || EfConstruction < Neighbors || Partitions is < 1 or > 10_000)
        {
            throw new ArgumentException($"Invalid vector index parameters: {this}.");
        }
    }
}

/// <summary>Owner-level operations on the vector index. Run by the installer and the benchmark, never by the API.</summary>
public interface IVectorIndexAdmin
{
    Task CreateIndexAsync(VectorIndexSpec spec, CancellationToken cancellationToken);

    Task DropIndexAsync(CancellationToken cancellationToken);

    /// <summary>Bytes the index occupies (Oracle vector pool usage for HNSW, segment size otherwise).</summary>
    Task<long> IndexSizeBytesAsync(CancellationToken cancellationToken);
}

/// <summary>Allow-list for schema and user names that have to be formatted into SQL text.</summary>
public static partial class SqlIdentifier
{
    public static string Validate(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return Pattern().IsMatch(identifier)
            ? identifier
            : throw new ArgumentException($"'{identifier}' is not an allowed SQL identifier (letters, digits, underscore; max 30).");
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,29}$")]
    private static partial Regex Pattern();
}
