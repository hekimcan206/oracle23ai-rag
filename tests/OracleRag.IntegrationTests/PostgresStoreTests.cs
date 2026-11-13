using Npgsql;
using OracleRag.Postgres;
using OracleRag.Retrieval;

namespace OracleRag.IntegrationTests;

public sealed class PostgresStoreTests(PostgresFixture postgres) : VectorStoreContract
{
    protected override IVectorStore Store => postgres.Store;

    protected override IVectorIndexAdmin Admin => postgres.Admin;

    protected override Task ResetAsync() => postgres.Admin.TruncateAsync(Ct);

    protected override async Task BulkLoadAsync(IReadOnlyList<(DocumentRecord Document, IReadOnlyList<ChunkRecord> Chunks)> items)
    {
        await postgres.Admin.BulkLoadAsync(items, Ct);
        await postgres.Admin.AnalyzeAsync(Ct);
    }

    [Fact]
    public async Task Approximate_query_shape_can_use_the_hnsw_index_and_the_exact_one_cannot()
    {
        await ResetAsync();
        await BulkLoadAsync([.. OracleRag.Synthetic.CorpusGenerator.Generate(60).Documents.Select(Rag.Prepare)]);
        await postgres.Admin.CreateIndexAsync(VectorIndexSpec.Hnsw(), Ct);

        // The test corpus is small enough for the planner to prefer a sequential scan, so it is discouraged where the
        // test asserts what a query *shape* allows the planner to do.
        const string PreferIndex = "SET LOCAL enable_seqscan = off";
        var approx = await PlanAsync(PgVectorStore.SearchSql(SearchMode.Approximate), PreferIndex);
        var exactWithoutGuard = await PlanAsync(PgVectorStore.SearchSql(SearchMode.Exact), PreferIndex);
        var exact = await PlanAsync(PgVectorStore.SearchSql(SearchMode.Exact), [.. PgVectorStore.Settings(new SearchQuery(new float[384], 5)).Select(s => $"SET LOCAL {s.Name} = {s.Value}")]);

        approx.Should().Contain($"Index Scan using {PgVectorAdmin.IndexName}");
        // The trap: a tie-breaker in ORDER BY does not keep the HNSW index out. The planner uses the index as the
        // presorted key of an Incremental Sort, and an "exact" query silently returns approximate neighbours.
        exactWithoutGuard.Should().Contain($"Index Scan using {PgVectorAdmin.IndexName}").And.Contain("Incremental Sort");
        // Hence the store's exact mode switches index scans off for the statement.
        exact.Should().NotContain(PgVectorAdmin.IndexName);
    }

    [Fact]
    public async Task The_application_role_cannot_change_schema()
    {
        await using var connection = await postgres.AppDataSource.OpenConnectionAsync(Ct);
        foreach (var sql in new[] { "CREATE TABLE rag.probe (x int)", "DROP TABLE rag.rag_chunk", "CREATE TABLE public.probe (x int)", "ALTER TABLE rag.rag_document ADD COLUMN x int" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await FluentActions.Awaiting(() => command.ExecuteNonQueryAsync(Ct)).Should().ThrowAsync<PostgresException>(sql);
            error.Which.SqlState.Should().BeOneOf(["42501"], sql);
        }
    }

    [Fact]
    public async Task Installer_is_idempotent_and_refuses_a_different_embedding_model()
    {
        var installer = new PgSchemaInstaller(postgres.OwnerDataSource);
        await installer.InstallAsync("rag_app", 384, OracleFixture.Embedder.ModelId, Ct);
        await FluentActions.Awaiting(() => installer.InstallAsync("rag_app", 384, "some-other-model", Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*lexproj-384*");
    }

    private async Task<string> PlanAsync(string sql, params string[] settings)
    {
        await using var connection = await postgres.OwnerDataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var setting in settings)
        {
            await using var set = new NpgsqlCommand(setting, connection, transaction);
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand("EXPLAIN " + sql, connection, transaction);
        explain.Parameters.Add(new() { Value = new Pgvector.Vector(OracleFixture.Embedder.Embed("car park fee")) });
        explain.Parameters.Add(new() { Value = 5 });
        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
