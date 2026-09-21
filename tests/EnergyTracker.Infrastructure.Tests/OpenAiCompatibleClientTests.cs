using System.Net;
using System.Text;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

public class OpenAiCompatibleClientTests
{
    // Captures the outgoing request so request-shaping assertions don't need a real HTTP listener.
    // `respond` receives the request's own CancellationToken (the linked timeout token
    // OpenAiCompatibleClient passes down) so the timeout scenario below can prove it actually
    // observes that token rather than just running to completion regardless.
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static OpenAiCompatibleClient NewSut(StubHandler handler, TimeSpan? requestTimeout = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        return new OpenAiCompatibleClient(httpClient, NullLogger<OpenAiCompatibleClient>.Instance, requestTimeout);
    }

    [Fact]
    public async Task Posts_to_v1_chat_completions_with_the_observed_directions_and_description_in_the_prompt()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Bump"}}]}""")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBe(AiPlausibilityDirection.Bump);
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.RequestUri.ShouldBe(new Uri("http://localhost:1234/v1/chat/completions"));
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody.ShouldContain("gaming session 3h");
        handler.LastRequestBody.ShouldContain("Bump");
        handler.LastRequestBody.ShouldContain("\"temperature\":0");
    }

    [Fact]
    public async Task Returns_Dip_when_the_model_classifies_a_dip()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Dip"}}]}""")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "away 2 weeks", [AiPlausibilityDirection.Dip], TestContext.Current.CancellationToken);

        result.ShouldBe(AiPlausibilityDirection.Dip);
    }

    [Fact]
    public async Task Returns_null_when_the_model_answers_None()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"None"}}]}""")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "watered the plants", [AiPlausibilityDirection.Bump, AiPlausibilityDirection.Dip], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_model_answers_a_direction_that_was_not_offered_as_observed()
    {
        // Defensive against a hallucinated answer outside the options actually given.
        var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Dip"}}]}""")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_on_malformed_response_body_instead_of_throwing()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "not json at all")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_response_has_no_choices_instead_of_throwing()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"choices":[]}""")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_on_a_non_2xx_status_instead_of_throwing()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.InternalServerError, "{}")));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_underlying_request_throws_instead_of_throwing()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_on_timeout_instead_of_throwing()
    {
        // Cancellation is cooperative — the delay must observe the request's own token (the
        // linked timeout token OpenAiCompatibleClient constructs) to actually unwind early; a
        // real network handler would abort the same way once that token fires. A short override
        // proves the timeout is self-imposed and bounded, without the test needing to wait out the
        // real 10s production default.
        var handler = new StubHandler(async (_, requestCancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), requestCancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Bump"}}]}""");
        });
        var sut = NewSut(handler, requestTimeout: TimeSpan.FromMilliseconds(50));

        var result = await sut.ClassifyAsync(
            "gaming session 3h", [AiPlausibilityDirection.Bump], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Never_calls_the_backend_when_no_directions_were_observed()
    {
        var called = false;
        var handler = new StubHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"None"}}]}"""));
        });
        var sut = NewSut(handler);

        var result = await sut.ClassifyAsync("gaming session 3h", [], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        called.ShouldBeFalse();
    }
}
