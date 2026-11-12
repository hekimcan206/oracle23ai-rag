using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using OracleRag.Api.Infrastructure;
using OracleRag.Chunking;
using OracleRag.Ingestion;
using OracleRag.Retrieval;

namespace OracleRag.Api.Features;

public sealed record DocumentRequest(string Title, string Markdown, string? Source = null);

public sealed record SearchRequest(string Query, int K = 5, SearchMode Mode = SearchMode.Exact, int? EfSearch = null, int? Probes = null);

public sealed record AskRequest(string Question, int K = 5, SearchMode Mode = SearchMode.Exact);

public sealed record WriteResponse(string Id, string Etag, bool Created, int Chunks);

public sealed record SearchHitResponse(int Rank, string ChunkId, string DocumentId, string Title, string Heading, string Text, double Distance);

public sealed record SearchResponse(string Store, SearchMode Mode, double RetrievalMs, IReadOnlyList<SearchHitResponse> Hits);

public sealed record CitationResponse(int Number, string ChunkId, string DocumentId, string Title, string Heading, double Distance);

public sealed record AskResponse(string Store, string Answer, bool Abstained, IReadOnlyList<CitationResponse> Citations, double RetrievalMs);

public sealed record StoresResponse(string DefaultStore, IReadOnlyCollection<string> Stores);

internal static partial class RagEndpoints
{
    public const int MaxTitleChars = 100;
    public const int MaxMarkdownChars = 200_000;
    public const int MaxQuestionChars = 1_000;

    public static RouteGroupBuilder MapRagEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/stores", (StoreRegistry registry) => TypedResults.Ok(new StoresResponse(registry.DefaultStore, registry.Names)))
            .WithSummary("Lists the vector stores this instance serves.");

        var store = group.MapGroup("/stores/{store}");
        store.MapPut("/documents/{id}", PutDocumentAsync)
            .WithSummary("Creates or replaces a document. Send If-Match with a previous ETag for a conditional replace.");
        store.MapGet("/documents/{id}", GetDocumentAsync)
            .WithSummary("Returns the document as the database renders it (Oracle duality view or PostgreSQL jsonb).");
        store.MapDelete("/documents/{id}", DeleteDocumentAsync).WithSummary("Deletes a document and its chunks.");
        store.MapPost("/search", SearchAsync).WithSummary("Nearest chunks by cosine distance, exact or through the vector index.");
        store.MapPost("/ask", AskAsync).WithSummary("Extractive answer with a citation, or an explicit abstention.");
        return group;
    }

    internal static async Task<Results<Created<WriteResponse>, Ok<WriteResponse>, ValidationProblem, NotFound, ProblemHttpResult>> PutDocumentAsync(
        string store, string id, DocumentRequest request, HttpContext http, StoreRegistry registry, RagService rag, CancellationToken cancellationToken)
    {
        if (registry.Find(store) is not { } target)
        {
            return TypedResults.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        if (!DocumentId().IsMatch(id))
        {
            errors["id"] = ["Use 1-200 lower-case letters, digits and hyphens, starting with a letter or digit."];
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleChars)
        {
            errors["title"] = [$"Required, at most {MaxTitleChars} characters."];
        }

        if (string.IsNullOrWhiteSpace(request.Markdown) || request.Markdown.Length > MaxMarkdownChars)
        {
            errors["markdown"] = [$"Required, at most {MaxMarkdownChars} characters."];
        }

        if (request.Source is { Length: > 50 })
        {
            errors["source"] = ["At most 50 characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var ifMatch = http.Request.Headers.IfMatch.ToString();
        var expected = string.IsNullOrEmpty(ifMatch) ? null : ifMatch.Trim('"');
        var source = new SourceDocument(id, request.Title.Trim(), request.Source ?? "api", request.Markdown);
        try
        {
            var (result, chunks) = await rag.IngestAsync(target, source, expected, cancellationToken);
            http.Response.Headers.ETag = $"\"{result.Etag}\"";
            var body = new WriteResponse(id, result.Etag, result.Created, chunks);
            return result.Created ? TypedResults.Created($"{http.Request.Path}", body) : TypedResults.Ok(body);
        }
        catch (EtagMismatchException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status412PreconditionFailed, title: "ETag mismatch");
        }
    }

    internal static async Task<Results<ContentHttpResult, NotFound>> GetDocumentAsync(
        string store, string id, HttpContext http, StoreRegistry registry, CancellationToken cancellationToken)
    {
        if (registry.Find(store) is not { } target || await target.GetAsync(id, cancellationToken) is not { } document)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ETag = $"\"{document.Etag}\"";
        return TypedResults.Content(document.Json, "application/json");
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteDocumentAsync(string store, string id, StoreRegistry registry, CancellationToken cancellationToken) =>
        registry.Find(store) is { } target && await target.DeleteAsync(id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    internal static async Task<Results<Ok<SearchResponse>, ValidationProblem, NotFound>> SearchAsync(
        string store, SearchRequest request, StoreRegistry registry, RagService rag, RagMetrics metrics, CancellationToken cancellationToken)
    {
        if (registry.Find(store) is not { } target)
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > MaxQuestionChars)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [$"Required, at most {MaxQuestionChars} characters."] });
        }

        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var hits = await rag.SearchAsync(target, request.Query, request.K, request.Mode, request.EfSearch, request.Probes, cancellationToken);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            metrics.Retrieval(target.Name, request.Mode.ToString(), ms);
            return TypedResults.Ok(new SearchResponse(
                target.Name,
                request.Mode,
                Math.Round(ms, 2),
                [.. hits.Select((h, i) => new SearchHitResponse(i + 1, h.ChunkId, h.DocumentId, h.Title, h.Heading, h.Text, h.Distance))]));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["search"] = [ex.Message] });
        }
    }

    internal static async Task<Results<Ok<AskResponse>, ValidationProblem, NotFound>> AskAsync(
        string store, AskRequest request, StoreRegistry registry, RagService rag, RagMetrics metrics, CancellationToken cancellationToken)
    {
        if (registry.Find(store) is not { } target)
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxQuestionChars)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["question"] = [$"Required, at most {MaxQuestionChars} characters."] });
        }

        try
        {
            var result = await rag.AskAsync(target, request.Question, request.K, request.Mode, cancellationToken);
            metrics.Retrieval(target.Name, request.Mode.ToString(), result.RetrievalMs);
            metrics.Answer(target.Name, result.Answer.Abstained);
            return TypedResults.Ok(new AskResponse(
                target.Name,
                result.Answer.Text,
                result.Answer.Abstained,
                [.. result.Answer.Citations.Select(c => new CitationResponse(c.Number, c.ChunkId, c.DocumentId, c.Title, c.Heading, c.Distance))],
                Math.Round(result.RetrievalMs, 2)));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ask"] = [ex.Message] });
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,199}$")]
    private static partial Regex DocumentId();
}
