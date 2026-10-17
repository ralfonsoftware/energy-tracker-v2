using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #1; AD-27, audit S9): every state-changing /api request needs X-Requested-With, enforced
// by one endpoint filter on the /api group. A header-less request is answered with a 400 ProblemDetails
// and the handler never runs.
public class RequestedWithFilterTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private const string FilterTitle = "Missing X-Requested-With header";

    private async Task<(HttpClient WithHeader, HttpClient WithoutHeader, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var subject = Guid.NewGuid().ToString();
        var withHeader = factory.CreateAuthenticatedClient(subject);
        var response = await withHeader.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (withHeader, factory.CreateAuthenticatedClient(subject, sendRequestedWith: false), created!.Id);
    }

    private static async Task AssertRejectedByTheFilterAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("title").GetString().ShouldBe(FilterTitle);
    }

    private static MultipartFormDataContent Upload() =>
        new() { { new ByteArrayContent([1, 2, 3]), "file", "export.csv" } };

    [Fact]
    public async Task A_JSON_POST_without_the_header_is_rejected_and_the_handler_does_not_run()
    {
        var (withHeader, withoutHeader, _) = await CreateHouseholdAsync();
        var name = $"Room {Guid.NewGuid():N}";

        var rejected = await withoutHeader.PostAsJsonAsync("/api/rooms", new { name }, TestContext.Current.CancellationToken);

        await AssertRejectedByTheFilterAsync(rejected);
        var rooms = await withHeader.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        rooms!.ShouldNotContain(r => r.Name == name);

        var accepted = await withHeader.PostAsJsonAsync("/api/rooms", new { name }, TestContext.Current.CancellationToken);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_DELETE_without_a_body_or_header_is_rejected_instead_of_answering_404()
    {
        var (withHeader, withoutHeader, _) = await CreateHouseholdAsync();
        var id = Guid.NewGuid();

        await AssertRejectedByTheFilterAsync(await withoutHeader.DeleteAsync($"/api/rooms/{id}", TestContext.Current.CancellationToken));

        (await withHeader.DeleteAsync($"/api/rooms/{id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_POST_without_a_body_or_header_is_rejected()
    {
        var (withHeader, withoutHeader, _) = await CreateHouseholdAsync();
        var path = $"/api/household-import/{Guid.NewGuid()}/confirm";

        await AssertRejectedByTheFilterAsync(await withoutHeader.PostAsync(path, content: null, TestContext.Current.CancellationToken));

        var reached = await withHeader.PostAsync(path, content: null, TestContext.Current.CancellationToken);
        await AssertNotTheFilterAsync(reached);
    }

    [Fact]
    public async Task A_PUT_with_a_body_but_no_header_is_rejected_and_changes_nothing()
    {
        var (withHeader, withoutHeader, householdId) = await CreateHouseholdAsync();

        var rejected = await withoutHeader.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken);

        await AssertRejectedByTheFilterAsync(rejected);
        var current = await withHeader.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        current!.Locale.ShouldBe("de-DE");
        (await withHeader.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_multipart_upload_without_the_header_is_rejected_and_queues_no_job()
    {
        var (withHeader, withoutHeader, householdId) = await CreateHouseholdAsync();

        var rejected = await withoutHeader.PostAsync("/api/smart-plug-imports", Upload(), TestContext.Current.CancellationToken);

        await AssertRejectedByTheFilterAsync(rejected);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
            (await db.BackgroundJobs.IgnoreQueryFilters().CountAsync(j => j.HouseholdId == householdId, TestContext.Current.CancellationToken))
                .ShouldBe(0);
        }

        await AssertNotTheFilterAsync(await withHeader.PostAsync("/api/smart-plug-imports", Upload(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_header_value_is_rejected_like_a_missing_one()
    {
        var (_, withoutHeader, _) = await CreateHouseholdAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms") { Content = JsonContent.Create(new { name = "Empty" }) };
        request.Headers.TryAddWithoutValidation("X-Requested-With", string.Empty);

        await AssertRejectedByTheFilterAsync(await withoutHeader.SendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GET_and_HEAD_need_no_header()
    {
        var (_, withoutHeader, _) = await CreateHouseholdAsync();

        (await withoutHeader.GetAsync("/api/status", TestContext.Current.CancellationToken)).StatusCode.ShouldNotBe(HttpStatusCode.BadRequest);
        using var head = new HttpRequestMessage(HttpMethod.Head, "/api/session");
        (await withoutHeader.SendAsync(head, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unauthenticated_POST_without_the_header_is_still_a_401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/rooms", new { name = "x" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // With the header, the request reaches its handler: any answer but the filter's own 400.
    private static async Task AssertNotTheFilterAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(FilterTitle);
    }
}
