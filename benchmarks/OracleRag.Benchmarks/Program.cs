using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Configurations;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using OracleRag.Answering;
using OracleRag.Chunking;
using OracleRag.Embeddings;
using OracleRag.Evaluation;
using OracleRag.Ingestion;
using OracleRag.Oracle;
using OracleRag.Postgres;
using OracleRag.Retrieval;
using OracleRag.Synthetic;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

// Same dataset, same vectors, same knobs, one database at a time, both limited to 2 CPUs (Oracle Database Free's own
// limit) so neither gets more hardware than the other. Every number is printed and written to benchmarks/results.
var entities = Arg("--entities", 3700);
var queryCount = Arg("--queries", 300);
var apiDocs = Arg("--api-docs", 300);
var output = ArgString("--out", Path.Combine("benchmarks", "results"));
var only = ArgString("--only", "postgres,oracle").Split(',');
const int K = 10;
const long Cpus = 2_000_000_000;

var embedder = new LexicalProjectionEmbedder();
var rag = new RagService(new MarkdownChunker(), embedder, new ExtractiveAnswerer());

Log($"generating corpus: {entities} entities");
var sw = Stopwatch.StartNew();
var corpus = CorpusGenerator.Generate(entities);
var items = corpus.Documents.Select(rag.Prepare).ToList();
var chunkCount = items.Sum(i => i.Chunks.Count);
Log($"{corpus.Documents.Count} documents, {chunkCount} chunks, embedded in {sw.Elapsed.TotalSeconds:F1} s");

// Ground truth: brute force in .NET over the same vectors.
var reference = new InMemoryVectorStore(embedder.Dimensions);
foreach (var (document, chunks) in items)
{
    await reference.UpsertAsync(document, chunks, null, CancellationToken.None);
}

var random = new Random(7);
var questions = corpus.Questions.OrderBy(_ => random.Next()).Take(queryCount).ToList();
var queryVectors = questions.Select(q => embedder.Embed(q.Question)).ToList();
var truth = new List<List<string>>();
var truthKth = new List<double>();
foreach (var v in queryVectors)
{
    var exactHits = await reference.SearchAsync(new SearchQuery(v, K), CancellationToken.None);
    truth.Add([.. exactHits.Select(h => h.ChunkId)]);
    truthKth.Add(exactHits[^1].Distance);
}

Log("ground truth computed");
var results = new List<StoreResult>();

if (only.Contains("postgres"))
{
    await using var pg = new PostgreSqlBuilder("pgvector/pgvector:0.8.1-pg17")
        .WithEnvironment("OWNER_PASSWORD", "OwnerBench1")
        .WithEnvironment("APP_PASSWORD", "AppBench1")
        .WithResourceMapping(Init("postgres"), "/docker-entrypoint-initdb.d/01-setup.sh", fileMode: Executable())
        .WithCommand("-c", "shared_buffers=512MB", "-c", "max_parallel_workers_per_gather=1", "-c", "max_parallel_maintenance_workers=1")
        .WithCreateParameterModifier(p => { p.HostConfig!.NanoCPUs = Cpus; p.HostConfig.Memory = 3L << 30; p.HostConfig.ShmSize = 1536L << 20; })
        .WithLabel("project", "oracle23ai-rag-bench")
        .Build();
    await pg.StartAsync();
    string Cs(string user, string pw) => new NpgsqlConnectionStringBuilder(pg.GetConnectionString()) { Username = user, Password = pw, Timeout = 60, CommandTimeout = 0 }.ConnectionString;
    var owner = PgVectorStore.CreateDataSource(Cs("rag_owner", "OwnerBench1"));
    var app = PgVectorStore.CreateDataSource(Cs("rag_app", "AppBench1"));
    await new PgSchemaInstaller(owner).InstallAsync("rag_app", embedder.Dimensions, embedder.ModelId);
    var admin = new PgVectorAdmin(owner, maintenanceWorkMemMb: 1024, parallelMaintenanceWorkers: 1);
    results.Add(await RunAsync(
        "postgres", "PostgreSQL 17 + pgvector 0.8.1", new PgVectorStore(app, embedder.Dimensions), admin,
        async () => { await admin.BulkLoadAsync(items, CancellationToken.None); await admin.AnalyzeAsync(CancellationToken.None); },
        () => admin.TruncateAsync(CancellationToken.None)));
}

if (only.Contains("oracle"))
{
    await using var oracle = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", "OwnerBench1")
        .WithEnvironment("APP_PASSWORD", "AppBench1")
        .WithEnvironment("VECTOR_MEMORY_SIZE", "512M")
        .WithResourceMapping(Init("oracle"), "/container-entrypoint-initdb.d/01-setup.sh", fileMode: Executable())
        .WithCreateParameterModifier(p => { p.HostConfig!.NanoCPUs = Cpus; p.HostConfig.Memory = 4L << 30; })
        .WithLabel("project", "oracle23ai-rag-bench")
        .Build();
    await oracle.StartAsync();
    string Cs(string user, string pw) => new OracleConnectionStringBuilder(oracle.GetConnectionString()) { UserID = user, Password = pw, ConnectionTimeout = 60 }.ConnectionString;
    await new OracleSchemaInstaller(Cs("rag_owner", "OwnerBench1")).InstallAsync("RAG_APP", embedder.Dimensions, embedder.ModelId);
    var admin = new OracleVectorAdmin(Cs("rag_owner", "OwnerBench1"));
    results.Add(await RunAsync(
        "oracle", "Oracle AI Database 26ai Free 23.26.3", new OracleVectorStore(Cs("rag_app", "AppBench1"), "RAG_OWNER", embedder.Dimensions), admin,
        async () => { await admin.BulkLoadAsync(items, 5_000, CancellationToken.None); await admin.GatherStatisticsAsync(CancellationToken.None); },
        () => admin.TruncateAsync(CancellationToken.None)));
}

var report = new Report(
    DateTimeOffset.UtcNow,
    new EnvironmentInfo(Environment.ProcessorCount, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, "2 CPUs per database container (NanoCPUs)"),
    corpus.Documents.Count, chunkCount, embedder.Dimensions, embedder.ModelId, questions.Count, K, results);
Directory.CreateDirectory(output);
var stamp = $"{chunkCount}chunks";
await File.WriteAllTextAsync(Path.Combine(output, $"bench-{stamp}.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllTextAsync(Path.Combine(output, $"bench-{stamp}.md"), Markdown(report));
Console.WriteLine(Markdown(report));
return 0;

async Task<StoreResult> RunAsync(string name, string version, IVectorStore store, IVectorIndexAdmin admin, Func<Task> bulkLoad, Func<Task> truncate)
{
    Log($"[{name}] API-path ingest of {apiDocs} documents");
    var ingest = new List<double>();
    foreach (var source in corpus.Documents.Take(apiDocs))
    {
        var t = Stopwatch.GetTimestamp();
        await rag.IngestAsync(store, source, null, CancellationToken.None);
        ingest.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }

    // Replace the same documents (update path: the duality view diffs chunks; PostgreSQL deletes and re-inserts).
    var replace = new List<double>();
    foreach (var source in corpus.Documents.Take(apiDocs))
    {
        var t = Stopwatch.GetTimestamp();
        await rag.IngestAsync(store, source with { Markdown = source.Markdown + "\n## Note\nUpdated.\n" }, null, CancellationToken.None);
        replace.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }

    await truncate();
    Log($"[{name}] bulk load {chunkCount} chunks");
    var bulkWatch = Stopwatch.StartNew();
    await bulkLoad();
    var bulkSeconds = bulkWatch.Elapsed.TotalSeconds;
    Log($"[{name}] bulk load {bulkSeconds:F1} s");

    var exact = await SweepAsync(store, "exact", new SearchQuery(new float[embedder.Dimensions], K), q => q);
    Log($"[{name}] exact p50 {exact.P50Ms:F2} ms recall {exact.Recall:F4}");

    var configs = new List<SweepResult> { exact };
    var builds = new List<BuildResult>();

    Log($"[{name}] HNSW build");
    var build = Stopwatch.StartNew();
    await admin.CreateIndexAsync(VectorIndexSpec.Hnsw(16, 64), CancellationToken.None);
    builds.Add(new BuildResult("HNSW m=16 efConstruction=64", build.Elapsed.TotalSeconds, await admin.IndexSizeBytesAsync(CancellationToken.None)));
    Log($"[{name}] HNSW built in {build.Elapsed.TotalSeconds:F1} s");
    foreach (var ef in new[] { 10, 20, 40, 80, 160 })
    {
        var r = await SweepAsync(store, $"HNSW ef_search={ef}", new SearchQuery(new float[embedder.Dimensions], K, SearchMode.Approximate, EfSearch: ef), q => q);
        configs.Add(r);
        Log($"[{name}] {r.Config}: recall {r.Recall:F4} p50 {r.P50Ms:F2} ms");
    }

    var rag40 = await RagQualityAsync(store, SearchMode.Approximate);
    await admin.DropIndexAsync(CancellationToken.None);

    var lists = (int)Math.Round(Math.Sqrt(chunkCount));
    Log($"[{name}] IVF build, {lists} partitions");
    build.Restart();
    await admin.CreateIndexAsync(VectorIndexSpec.Ivf(lists), CancellationToken.None);
    builds.Add(new BuildResult($"IVF partitions={lists}", build.Elapsed.TotalSeconds, await admin.IndexSizeBytesAsync(CancellationToken.None)));
    Log($"[{name}] IVF built in {build.Elapsed.TotalSeconds:F1} s");
    foreach (var probes in new[] { 1, 4, 16, 64 })
    {
        var r = await SweepAsync(store, $"IVF probes={probes}", new SearchQuery(new float[embedder.Dimensions], K, SearchMode.Approximate, Probes: probes), q => q);
        configs.Add(r);
        Log($"[{name}] {r.Config}: recall {r.Recall:F4} p50 {r.P50Ms:F2} ms");
    }

    await admin.DropIndexAsync(CancellationToken.None);
    var ragExact = await RagQualityAsync(store, SearchMode.Exact);

    return new StoreResult(name, version, Summary(ingest), Summary(replace), bulkSeconds, chunkCount / bulkSeconds, builds, configs, ragExact, rag40);
}

async Task<SweepResult> SweepAsync(IVectorStore store, string config, SearchQuery template, Func<SearchQuery, SearchQuery> _)
{
    // Warm-up: connection pool, cursor cache, index pages / vector pool.
    for (var i = 0; i < Math.Min(30, queryVectors.Count); i++)
    {
        await store.SearchAsync(template with { Vector = queryVectors[i] }, CancellationToken.None);
    }

    var latencies = new List<double>();
    var recall = new List<double>();
    var distanceRecall = new List<double>();
    var agreeTop1 = 0;
    for (var i = 0; i < queryVectors.Count; i++)
    {
        var t = Stopwatch.GetTimestamp();
        var hits = await store.SearchAsync(template with { Vector = queryVectors[i] }, CancellationToken.None);
        latencies.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        var ids = hits.Select(h => h.ChunkId).ToList();
        recall.Add(Metrics.OverlapRecall(ids, truth[i], K));
        // ann-benchmarks style: a returned neighbour counts when it is no farther than the true k-th neighbour (ties).
        distanceRecall.Add(hits.Count(h => h.Distance <= truthKth[i] + 1e-5) / (double)K);
        agreeTop1 += ids.Count > 0 && ids[0] == truth[i][0] ? 1 : 0;
    }

    return new SweepResult(config, recall.Average(), distanceRecall.Average(), agreeTop1 / (double)queryVectors.Count, Metrics.Median(latencies), Metrics.Percentile(latencies, 95), Metrics.Percentile(latencies, 99));
}

async Task<RagQuality> RagQualityAsync(IVectorStore store, SearchMode mode)
{
    // With the default HNSW knobs (no EFSEARCH / ef_search given): what an application gets out of the box.
    double hit1 = 0, answered = 0, abstained = 0;
    var answerable = questions.Where(q => q.Answerable).ToList();
    var unanswerable = questions.Where(q => !q.Answerable).ToList();
    foreach (var q in answerable)
    {
        var r = await rag.AskAsync(store, q.Question, 5, mode, CancellationToken.None);
        hit1 += Metrics.HitAtK([.. r.Hits.Select(h => h.ChunkId)], q.ExpectedChunkId!, 1);
        answered += !r.Answer.Abstained && r.Answer.Citations[0].ChunkId == q.ExpectedChunkId && r.Answer.Text.Contains(q.ExpectedAnswer!, StringComparison.Ordinal) ? 1 : 0;
    }

    foreach (var q in unanswerable)
    {
        abstained += (await rag.AskAsync(store, q.Question, 5, mode, CancellationToken.None)).Answer.Abstained ? 1 : 0;
    }

    return new RagQuality(mode.ToString(), answerable.Count, hit1 / answerable.Count, answered / answerable.Count, unanswerable.Count, unanswerable.Count == 0 ? 1 : abstained / unanswerable.Count);
}

static LatencySummary Summary(List<double> sample) => new(sample.Count, Metrics.Median(sample), Metrics.Percentile(sample, 95));

static string Markdown(Report r)
{
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.AppendLine(inv, $"# Benchmark: {r.Chunks:N0} chunks, {r.Dimensions} dimensions, k = {r.K}, {r.Queries} queries");
    sb.AppendLine();
    sb.AppendLine(inv, $"Run {r.When:u}. Client: {r.Environment.LogicalCpus} logical CPUs, {r.Environment.Os}, {r.Environment.Runtime}. Databases: {r.Environment.Limits}. Embedder `{r.Model}`.");
    sb.AppendLine();
    sb.AppendLine("## Writes");
    sb.AppendLine();
    sb.AppendLine("| store | API create p50 / p95 (ms) | API replace p50 / p95 (ms) | bulk load (s) | bulk chunks/s |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var s in r.Stores)
    {
        sb.AppendLine(inv, $"| {s.Version} | {s.ApiCreate.P50Ms:F2} / {s.ApiCreate.P95Ms:F2} | {s.ApiReplace.P50Ms:F2} / {s.ApiReplace.P95Ms:F2} | {s.BulkSeconds:F1} | {s.BulkChunksPerSecond:N0} |");
    }

    sb.AppendLine();
    sb.AppendLine("## Index builds");
    sb.AppendLine();
    sb.AppendLine("| store | index | build (s) | size (MB) |");
    sb.AppendLine("|---|---|---|---|");
    foreach (var s in r.Stores)
    {
        foreach (var b in s.Builds)
        {
            sb.AppendLine(inv, $"| {s.Name} | {b.Index} | {b.Seconds:F1} | {b.SizeBytes / 1024.0 / 1024.0:F1} |");
        }
    }

    sb.AppendLine();
    sb.AppendLine("## Search (single client, sequential, latency includes the round trip)");
    sb.AppendLine();
    sb.AppendLine("| store | configuration | recall@10 (ids) | recall@10 (distance) | top-1 = exact | p50 (ms) | p95 (ms) | p99 (ms) |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|");
    foreach (var s in r.Stores)
    {
        foreach (var c in s.Searches)
        {
            sb.AppendLine(inv, $"| {s.Name} | {c.Config} | {c.Recall:F4} | {c.DistanceRecall:F4} | {c.Top1Agreement:P1} | {c.P50Ms:F2} | {c.P95Ms:F2} | {c.P99Ms:F2} |");
        }
    }

    sb.AppendLine();
    sb.AppendLine("## RAG quality on the sampled questions");
    sb.AppendLine();
    sb.AppendLine("| store | search | answerable | hit@1 | cited + correct answer | unanswerable | abstained |");
    sb.AppendLine("|---|---|---|---|---|---|---|");
    foreach (var s in r.Stores)
    {
        foreach (var q in new[] { s.RagExact, s.RagHnswDefault })
        {
            var label = q.Mode == "Exact" ? "exact" : "HNSW, default knobs";
            sb.AppendLine(inv, $"| {s.Name} | {label} | {q.Answerable} | {q.HitAt1:P1} | {q.Correct:P1} | {q.Unanswerable} | {q.Abstained:P1} |");
        }
    }

    return sb.ToString();
}

static byte[] Init(string db) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "init", db, "01-setup.sh"));

static UnixFileModes Executable() => UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

static void Log(string message) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

int Arg(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
}

string ArgString(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

internal sealed record LatencySummary(int Count, double P50Ms, double P95Ms);

internal sealed record BuildResult(string Index, double Seconds, long SizeBytes);

internal sealed record SweepResult(string Config, double Recall, double DistanceRecall, double Top1Agreement, double P50Ms, double P95Ms, double P99Ms);

internal sealed record RagQuality(string Mode, int Answerable, double HitAt1, double Correct, int Unanswerable, double Abstained);

internal sealed record StoreResult(string Name, string Version, LatencySummary ApiCreate, LatencySummary ApiReplace, double BulkSeconds, double BulkChunksPerSecond, IReadOnlyList<BuildResult> Builds, IReadOnlyList<SweepResult> Searches, RagQuality RagExact, RagQuality RagHnswDefault);

internal sealed record EnvironmentInfo(int LogicalCpus, string Os, string Runtime, string Limits);

internal sealed record Report(DateTimeOffset When, EnvironmentInfo Environment, int Documents, int Chunks, int Dimensions, string Model, int Queries, int K, IReadOnlyList<StoreResult> Stores);
