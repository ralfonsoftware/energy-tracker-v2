using EnergyTracker.Application;

namespace EnergyTracker.Api.Tests;

// Story 11.22: the four requests of AC #2 with a known token, shared by the log and trace tests.
internal static class InviteTokenRequests
{
    public static string NewToken() => InviteToken.Generate();

    // Path and query as the request lines show them after redaction.
    public static readonly string[] RedactedPathsAndQueries =
    [
        "/join/{token}",
        "/api/household-invites/{token}",
        "/api/household-invites/{token}/accept",
        "/login?returnUrl=%2Fjoin%2F{token}",
    ];

    public static HttpClient CreateClient(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Requested-With", "fetch");
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);
        return client;
    }

    public static async Task FireAsync(HttpClient client, string token)
    {
        var ct = TestContext.Current.CancellationToken;
        using (await client.GetAsync($"/join/{token}", ct)) { }
        using (await client.GetAsync($"/api/household-invites/{token}", ct)) { }
        using (await client.PostAsync($"/api/household-invites/{token}/accept", null, ct)) { }
        using (await client.GetAsync($"/login?returnUrl=%2Fjoin%2F{token}", ct)) { }
    }
}
