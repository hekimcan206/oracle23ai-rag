using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OracleRag.UnitTests.Api;

/// <summary>The HTTP contract, against the in-memory store (the database stores are covered by the integration suite).</summary>
public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    private readonly HttpClient _client;

    public ApiTests(Factory factory) => _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Rag:EnableMemoryStore", "true");
            builder.UseSetting("Rag:DefaultStore", "memory");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }

    private static object Doc(string body = "## Car park\nThe car park at Testkent Clinic charges 40 TL per hour.") =>
        new { title = "Testkent Clinic — Parking", markdown = body };

    [Fact]
    public async Task Lists_the_configured_stores()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/v1/stores", Ct);
        body.GetProperty("defaultStore").GetString().Should().Be("memory");
        body.GetProperty("stores").EnumerateArray().Select(e => e.GetString()).Should().Equal("memory");
    }

    [Fact]
    public async Task Put_creates_then_replaces_and_returns_an_etag()
    {
        var created = await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-create", Doc(), Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.ETag.Should().NotBeNull();

        var replaced = await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-create", Doc("## A\nOther text."), Ct);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        replaced.Headers.ETag!.Tag.Should().NotBe(created.Headers.ETag!.Tag);
        (await replaced.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chunks").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Stale_if_match_is_a_412_and_the_current_one_succeeds()
    {
        var first = await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-etag", Doc(), Ct);
        var etag = first.Headers.ETag!.Tag;
        await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-etag", Doc("## B\nChanged by someone else."), Ct);

        using var stale = new HttpRequestMessage(HttpMethod.Put, "/v1/stores/memory/documents/api-etag") { Content = JsonContent.Create(Doc()) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        (await _client.SendAsync(stale, Ct)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        var current = await _client.GetAsync("/v1/stores/memory/documents/api-etag", Ct);
        using var fresh = new HttpRequestMessage(HttpMethod.Put, "/v1/stores/memory/documents/api-etag") { Content = JsonContent.Create(Doc()) };
        fresh.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        (await _client.SendAsync(fresh, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_returns_the_store_json_and_delete_removes_it()
    {
        await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-get", Doc(), Ct);
        var get = await _client.GetAsync("/v1/stores/memory/documents/api-get", Ct);
        get.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await get.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("_id").GetString().Should().Be("api-get");

        (await _client.DeleteAsync("/v1/stores/memory/documents/api-get", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.DeleteAsync("/v1/stores/memory/documents/api-get", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/v1/stores/memory/documents/api-get", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("-leading-hyphen")]
    public async Task Invalid_document_ids_are_rejected(string id) =>
        (await _client.PutAsJsonAsync($"/v1/stores/memory/documents/{id}", Doc(), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Missing_title_and_oversized_markdown_are_rejected()
    {
        var response = await _client.PutAsJsonAsync("/v1/stores/memory/documents/api-bad", new { title = "", markdown = new string('x', 200_001) }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("title", out _).Should().BeTrue();
        errors.TryGetProperty("markdown", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_store_is_a_404_not_a_fallback()
    {
        (await _client.PostAsJsonAsync("/v1/stores/mongodb/ask", new { question = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(10, 5)]
    public async Task Out_of_range_search_knobs_are_a_400(int k, int? efSearch) =>
        (await _client.PostAsJsonAsync("/v1/stores/memory/search", new { query = "parking", k, mode = "Approximate", efSearch }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Ask_answers_with_a_citation_and_search_returns_ranked_hits()
    {
        await _client.PutAsJsonAsync("/v1/stores/memory/documents/askkent-clinic-parking", Doc("## Car park\nThe car park at Askkent Clinic charges 55 TL per hour."), Ct);

        var ask = await _client.PostAsJsonAsync("/v1/stores/memory/ask", new { question = "How much does the car park at Askkent Clinic charge per hour?" }, Ct);
        var answer = await ask.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("abstained").GetBoolean().Should().BeFalse();
        answer.GetProperty("answer").GetString().Should().Contain("55 TL").And.EndWith("[1]");
        answer.GetProperty("citations")[0].GetProperty("chunkId").GetString().Should().Be("askkent-clinic-parking#0000");

        var search = await _client.PostAsJsonAsync("/v1/stores/memory/search", new { query = "Testkent car park", k = 3 }, Ct);
        var hits = (await search.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("hits");
        hits[0].GetProperty("rank").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Ask_without_a_question_is_a_400() =>
        (await _client.PostAsJsonAsync("/v1/stores/memory/ask", new { question = " " }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Liveness_runs_no_dependency_checks() =>
        (await _client.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
}
