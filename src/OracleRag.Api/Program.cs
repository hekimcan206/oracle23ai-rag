using System.Diagnostics;
using System.Text.Json.Serialization;
using OracleRag.Answering;
using OracleRag.Api;
using OracleRag.Api.Features;
using OracleRag.Api.Infrastructure;
using OracleRag.Chunking;
using OracleRag.Embeddings;
using OracleRag.Ingestion;
using OracleRag.Oracle;
using OracleRag.Postgres;
using OracleRag.Retrieval;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

var options = builder.Configuration.GetSection("Rag").Get<RagOptions>() ?? new RagOptions();
var embedder = new LexicalProjectionEmbedder(options.Dimensions);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IEmbedder>(embedder);
builder.Services.AddSingleton(new MarkdownChunker());
builder.Services.AddSingleton(new ExtractiveAnswerer());
builder.Services.AddSingleton<RagService>();

// Each store is optional: it is served when its application connection string is configured.
var health = builder.Services.AddHealthChecks();
var stores = new List<IVectorStore>();
var oracleApp = builder.Configuration.GetConnectionString("OracleApp");
if (!string.IsNullOrWhiteSpace(oracleApp))
{
    stores.Add(new OracleVectorStore(oracleApp, options.Oracle.SchemaOwner, options.Dimensions));
    health.AddCheck("oracle", new OracleHealthCheck(oracleApp), tags: [HealthEndpoints.ReadyTag]);
}

var postgresApp = builder.Configuration.GetConnectionString("PostgresApp");
if (!string.IsNullOrWhiteSpace(postgresApp))
{
    var dataSource = PgVectorStore.CreateDataSource(postgresApp);
    builder.Services.AddSingleton(dataSource);
    stores.Add(new PgVectorStore(dataSource, options.Dimensions));
    health.AddCheck("postgres", new PostgresHealthCheck(dataSource), tags: [HealthEndpoints.ReadyTag]);
}

if (options.EnableMemoryStore)
{
    stores.Add(new InMemoryVectorStore(options.Dimensions));
}

var defaultStore = stores.Any(s => s.Name == options.DefaultStore) ? options.DefaultStore : stores.FirstOrDefault()?.Name ?? "none";
builder.Services.AddSingleton(new StoreRegistry(stores, defaultStore));

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
// A document is capped at 200k characters; 1 MB of request body leaves room for JSON escaping and nothing more.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "oracle23ai-rag API";
    document.Info.Version = "v1";
    document.Info.Description = "RAG over Oracle AI Vector Search (JSON Relational Duality) and pgvector, with the same synthetic data in both.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, two roles: `seed` installs both schemas with the owner accounts, loads the synthetic corpus and builds
// the HNSW indexes, then exits; the long-running API connects with the least-privileged application accounts.
if (args.Contains("seed"))
{
    return await Seeder.RunAsync(app.Configuration, options, app.Services.GetRequiredService<RagService>(), app.Logger);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(o =>
    o.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("oracle23ai-rag API"));
}

app.MapHealthEndpoints();
app.MapGroup("/v1").WithGroupName("v1").MapRagEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
