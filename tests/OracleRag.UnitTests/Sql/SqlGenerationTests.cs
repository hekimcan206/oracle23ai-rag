using System.Text;
using OracleRag.Chunking;
using OracleRag.Oracle;
using OracleRag.Postgres;
using OracleRag.Retrieval;
using OracleRag.UnitTests.Golden;

namespace OracleRag.UnitTests.Sql;

public sealed class SqlGenerationTests
{
    private static readonly float[] V = new float[4];

    [Fact]
    public void Oracle_exact_search_binds_k_and_approximate_search_formats_only_validated_integers()
    {
        var exact = OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 5));
        exact.Should().Contain("FETCH EXACT FIRST :k ROWS ONLY").And.Contain("VECTOR_DISTANCE(c.embedding, :q, COSINE)");

        OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 5, SearchMode.Approximate))
            .Should().Contain("FETCH APPROX FIRST 5 ROWS ONLY").And.NotContain("PARAMETERS");
        OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 10, SearchMode.Approximate, EfSearch: 64))
            .Should().Contain("FETCH APPROX FIRST 10 ROWS ONLY WITH TARGET ACCURACY PARAMETERS (EFSEARCH 64)");
        OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 10, SearchMode.Approximate, Probes: 8))
            .Should().Contain("PARAMETERS (NEIGHBOR PARTITION PROBES 8)");
    }

    [Fact]
    public void Postgres_approximate_search_orders_by_the_distance_expression_alone()
    {
        PgVectorStore.SearchSql(SearchMode.Approximate).Should().Contain("ORDER BY c.embedding <=> $1\n");
        PgVectorStore.SearchSql(SearchMode.Exact).Should().Contain("ORDER BY dist, c.chunk_id");
        PgVectorStore.Settings(new SearchQuery(V, 5)).Should().Equal(("enable_indexscan", "off"));
        PgVectorStore.Settings(new SearchQuery(V, 5, SearchMode.Approximate, 40, 3)).Should().Equal(("hnsw.ef_search", "40"), ("ivfflat.probes", "3"));
    }

    [Fact]
    public void Store_rejects_an_owner_name_outside_the_identifier_allow_list()
    {
        FluentActions.Invoking(() => new OracleVectorStore("Data Source=x", "rag_owner; DROP TABLE x", 4)).Should().Throw<ArgumentException>();
        SqlIdentifier.Validate("RAG_OWNER").Should().Be("RAG_OWNER");
    }

    [Fact]
    public void Index_parameters_are_range_checked_before_they_reach_ddl()
    {
        FluentActions.Invoking(() => OracleVectorAdmin.CreateIndexSql(VectorIndexSpec.Hnsw(neighbors: 1))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PgVectorAdmin.CreateIndexSql(VectorIndexSpec.Ivf(0))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PgVectorAdmin.CreateIndexSql(VectorIndexSpec.Hnsw(32, 16))).Should().Throw<ArgumentException>("ef_construction below m");
    }

    [Fact]
    public void Duality_document_carries_the_expected_etag_only_on_conditional_writes()
    {
        var doc = new DocumentRecord("d", "Title \"quoted\"", "s");
        ChunkRecord[] chunks = [new("d#0000", 0, "H", "Body with ünicode", "abc", [1f])];

        var plain = OracleVectorStore.DocumentJson(doc, chunks, null);
        var conditional = OracleVectorStore.DocumentJson(doc, chunks, "ETAG1");

        plain.Should().NotContain("_metadata");
        conditional.Should().Contain("\"_metadata\":{\"etag\":\"ETAG1\"}");
        var parsed = OracleVectorStore.ParseDocument(conditional);
        parsed.Title.Should().Be("Title \"quoted\"");
        parsed.Chunks.Single().Text.Should().Be("Body with ünicode");
        parsed.Etag.Should().Be("ETAG1");
    }

    [Fact]
    public void Script_splitter_drops_comments_and_separators()
    {
        SqlScripts.Split("-- comment\nCREATE TABLE a (x NUMBER)\n/\n-- only a comment\n/\nGRANT SELECT ON a TO b\n/\n")
            .Should().Equal("CREATE TABLE a (x NUMBER)", "GRANT SELECT ON a TO b");
    }

    [Fact]
    public void Generated_sql_is_unchanged()
    {
        // Every statement the stores and admins generate, side by side for both databases, as an approval file.
        var sb = new StringBuilder();
        void Add(string title, string sql) => sb.Append("-- ").AppendLine(title).AppendLine(sql.ReplaceLineEndings("\n").Trim()).AppendLine();
        Add("oracle exact search", OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 5)));
        Add("oracle approximate search, HNSW ef 64", OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(V, 10, SearchMode.Approximate, 64)));
        Add("oracle HNSW index", OracleVectorAdmin.CreateIndexSql(VectorIndexSpec.Hnsw()));
        Add("oracle IVF index", OracleVectorAdmin.CreateIndexSql(VectorIndexSpec.Ivf(64)));
        Add("postgres exact search", PgVectorStore.SearchSql(SearchMode.Exact));
        Add("postgres approximate search", PgVectorStore.SearchSql(SearchMode.Approximate));
        Add("postgres HNSW index", PgVectorAdmin.CreateIndexSql(VectorIndexSpec.Hnsw()));
        Add("postgres IVF index", PgVectorAdmin.CreateIndexSql(VectorIndexSpec.Ivf(64)));
        GoldenFile.Verify("generated-sql.txt", sb.ToString());
    }

    [Fact]
    public void Chunker_output_for_a_corpus_document_is_unchanged()
    {
        var document = OracleRag.Synthetic.CorpusGenerator.Generate(1).Documents[0];
        var chunks = new MarkdownChunker().Split(document);
        GoldenFile.Verify("chunks-first-document.json", System.Text.Json.JsonSerializer.Serialize(chunks, GoldenFile.Options));
    }
}
