using System.Diagnostics;
using EnergyTracker.Application;
using Serilog.Events;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.22 (AD-26, audit S10): pure rules for rewriting invite segments. ADR 0008.
public class InviteTokenRedactorTests
{
    private static readonly string Token = InviteToken.Generate();

    [Theory]
    [InlineData("/join/{0}", "/join/{token}")]
    [InlineData("/join/{0}/", "/join/{token}/")]
    [InlineData("/JOIN/{0}", "/JOIN/{token}")]
    [InlineData("/api/household-invites/{0}", "/api/household-invites/{token}")]
    [InlineData("/api/household-invites/{0}/accept", "/api/household-invites/{token}/accept")]
    [InlineData("/login?returnUrl=%2Fjoin%2F{0}", "/login?returnUrl=%2Fjoin%2F{token}")]
    [InlineData("/login?returnUrl=%2fjoin%2f{0}", "/login?returnUrl=%2fjoin%2f{token}")]
    [InlineData("/login?returnUrl=%2Fjoin%2F{0}&x=1", "/login?returnUrl=%2Fjoin%2F{token}&x=1")]
    [InlineData("/login?returnUrl=%252Fjoin%252F{0}", "/login?returnUrl=%252Fjoin%252F{token}")]
    [InlineData("http://host/join/{0}?x=1", "http://host/join/{token}?x=1")]
    [InlineData("http://host/join/{0}#frag", "http://host/join/{token}#frag")]
    [InlineData("GET /join/{0} then more", "GET /join/{token} then more")]
    [InlineData("opened \"/join/{0}\".", "opened \"/join/{token}\".")]
    [InlineData("/api/household-invites/not-a-token!?x=1", "/api/household-invites/{token}?x=1")]
    // Fail closed (Task 2.1): characters outside base64url and extra slashes cannot leave a readable tail.
    [InlineData("/join/{0}.tail,more:x@y", "/join/{token}")]
    [InlineData("/join//{0}", "/join//{token}")]
    [InlineData("/api/household-invites//{0}/accept", "/api/household-invites//{token}/accept")]
    public void Rewrites_the_segment_after_an_invite_prefix(string template, string expectedTemplate)
    {
        var input = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Token);

        var result = InviteTokenRedactor.Redact(input);

        result.ShouldBe(expectedTemplate);
        result.ShouldNotContain(Token);
    }

    [Theory]
    [InlineData("/api/meter-readings")]
    [InlineData("/health")]
    [InlineData("/api/household-invites")]
    [InlineData("/api/household-invites?x=1")]
    [InlineData("/api/household-invites/")]
    [InlineData("/join")]
    [InlineData("/join/")]
    [InlineData("/api/household-members/6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("")]
    public void Leaves_non_invite_input_unchanged(string input)
    {
        InviteTokenRedactor.Redact(input).ShouldBe(input);
    }

    [Theory]
    [InlineData("/join/{0}")]
    [InlineData("/login?returnUrl=%2Fjoin%2F{0}&x=1")]
    [InlineData("/api/household-invites/{0}/accept")]
    public void Is_idempotent(string template)
    {
        var once = InviteTokenRedactor.Redact(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Token));

        InviteTokenRedactor.Redact(once).ShouldBe(once);
    }

    [Fact]
    public void Known_bad_sample_a_raw_token_is_rewritten()
    {
        // Non-vacuous: the input does carry the token, so a redactor that returns it unchanged fails here.
        var raw = $"Request starting HTTP/1.1 GET http://localhost/join/{Token}";

        raw.ShouldContain(Token);
        InviteTokenRedactor.Redact(raw).ShouldNotContain(Token);
    }

    [Fact]
    public void Span_hook_rewrites_a_raw_returnUrl_in_url_query()
    {
        // Proves the hook itself, independent of the instrumentation's own default query redaction (ADR 0008).
        using var activity = new Activity("test");
        activity.SetTag("url.query", $"?returnUrl=%2Fjoin%2F{Token}");
        activity.SetTag("url.path", $"/join/{Token}");

        InviteTokenSpanRedaction.RedactUrlTags(activity);

        activity.GetTagItem("url.query").ShouldBe("?returnUrl=%2Fjoin%2F{token}");
        activity.GetTagItem("url.path").ShouldBe("/join/{token}");
    }

    [Fact]
    public void Enricher_rewrites_uri_scalars_and_dictionary_keys()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            MessageTemplate.Empty,
            [
                new LogEventProperty("Uri", new ScalarValue(new Uri($"http://host/join/{Token}"))),
                new LogEventProperty("Map", new DictionaryValue([KeyValuePair.Create(new ScalarValue($"/join/{Token}"), (LogEventPropertyValue)new ScalarValue(1))])),
                new LogEventProperty("Plain", new ScalarValue("/health")),
            ]);
        var untouched = logEvent.Properties["Plain"];

        new InviteTokenRedactingEnricher().Enrich(logEvent, null!);

        logEvent.Properties.Values.Select(v => v.ToString()).ShouldAllBe(v => !v.Contains(Token));
        logEvent.Properties["Uri"].ToString().ShouldContain("/join/{token}");
        logEvent.Properties["Plain"].ShouldBeSameAs(untouched);
    }
}
