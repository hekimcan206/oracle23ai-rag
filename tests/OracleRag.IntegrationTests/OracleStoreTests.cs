using Oracle.ManagedDataAccess.Client;
using OracleRag.Chunking;
using OracleRag.Oracle;
using OracleRag.Retrieval;

namespace OracleRag.IntegrationTests;

public sealed class OracleStoreTests(OracleFixture oracle) : VectorStoreContract
{
    protected override IVectorStore Store => oracle.Store;

    protected override IVectorIndexAdmin Admin => oracle.Admin;

    protected override Task ResetAsync() => oracle.Admin.TruncateAsync(Ct);

    protected override async Task BulkLoadAsync(IReadOnlyList<(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks)> items)
    {
        await oracle.Admin.BulkLoadAsync(items, 2_000, Ct);
        await oracle.Admin.GatherStatisticsAsync(Ct);
    }

    [Fact]
    public async Task One_json_write_through_the_duality_view_becomes_rows_in_both_tables_with_vectors()
    {
        await ResetAsync();
        var source = new SourceDocument("duality-rows", "Dualkent Clinic — Pharmacy", "test", "## Hours\nOpen 8:00 to 20:00.\n## Prescriptions\nKept for 10 days.");
        await Rag.IngestAsync(Store, source, null, Ct);

        (await ScalarAsync(oracle.OwnerConnectionString, "SELECT COUNT(*) FROM rag_document WHERE doc_id = 'duality-rows'")).Should().Be(1);
        (await ScalarAsync(oracle.OwnerConnectionString, "SELECT COUNT(*) FROM rag_chunk WHERE doc_id = 'duality-rows'")).Should().Be(2);
        (await ScalarAsync(oracle.OwnerConnectionString, "SELECT COUNT(*) FROM rag_chunk WHERE embedding IS NULL")).Should().Be(0, "no committed chunk is without its vector");
        (await ScalarAsync(oracle.OwnerConnectionString, "SELECT VECTOR_DIMENSION_COUNT(embedding) FROM rag_chunk WHERE ROWNUM = 1")).Should().Be(384);

        var view = await Store.GetAsync("duality-rows", Ct);
        view!.Json.Should().Contain("\"_metadata\":{\"etag\":").And.Contain("\"asof\":", "the document is rendered by the duality view itself");
    }

    [Fact]
    public async Task Approximate_search_is_answered_from_the_hnsw_index()
    {
        await ResetAsync();
        await BulkLoadAsync([.. OracleRag.Synthetic.CorpusGenerator.Generate(20).Documents.Select(Rag.Prepare)]);
        await oracle.Admin.CreateIndexAsync(VectorIndexSpec.Hnsw(), Ct);

        var exact = await PlanAsync(OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(new float[384], 5)));
        var approx = await PlanAsync(OracleVectorStore.SearchSql("RAG_OWNER", new SearchQuery(new float[384], 5, SearchMode.Approximate, EfSearch: 40)));

        approx.Should().Contain("VECTOR INDEX HNSW SCAN");
        exact.Should().NotContain("VECTOR INDEX");
    }

    [Fact]
    public async Task The_application_account_cannot_change_schema_or_bypass_the_duality_view()
    {
        await using var connection = new OracleConnection(oracle.AppConnectionString);
        await connection.OpenAsync(Ct);

        foreach (var sql in new[]
        {
            "CREATE TABLE rag_app_probe (x NUMBER)",
            "INSERT INTO rag_owner.rag_document (doc_id, title, source) VALUES ('x', 'x', 'x')",
            "UPDATE rag_owner.rag_chunk SET body = 'tampered'",
            "DELETE FROM rag_owner.rag_chunk",
            "DROP INDEX rag_owner.rag_chunk_vec_ix",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var error = await FluentActions.Awaiting(() => command.ExecuteNonQueryAsync(Ct)).Should().ThrowAsync<OracleException>(sql);
            error.Which.Number.Should().BeOneOf([1031, 942, 1418, 41900], sql);
        }
    }

    [Fact]
    public async Task Installer_is_idempotent_and_refuses_a_different_embedding_model()
    {
        var installer = new OracleSchemaInstaller(oracle.OwnerConnectionString);
        await installer.InstallAsync("RAG_APP", 384, OracleFixture.Embedder.ModelId, Ct);
        await FluentActions.Awaiting(() => installer.InstallAsync("RAG_APP", 384, "some-other-model", Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*lexproj-384*");
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> PlanAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using (var explain = connection.CreateCommand())
        {
            explain.CommandText = "EXPLAIN PLAN FOR " + sql;
            explain.BindByName = true;
            explain.Parameters.Add("q", OracleDbType.Vector_Float32, new float[384], System.Data.ParameterDirection.Input);
            if (sql.Contains(":k", StringComparison.Ordinal))
            {
                explain.Parameters.Add("k", OracleDbType.Int32, 5, System.Data.ParameterDirection.Input);
            }

            await explain.ExecuteNonQueryAsync(Ct);
        }

        await using var display = connection.CreateCommand();
        display.CommandText = "SELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY(format => 'BASIC'))";
        var lines = new List<string>();
        await using var reader = await display.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
