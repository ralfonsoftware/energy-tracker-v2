using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public partial class InviteTokenTests
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Base64UrlToken();

    [Fact]
    public void Generate_returns_43_base64url_characters_without_padding_or_unsafe_symbols()
    {
        var token = InviteToken.Generate();

        Base64UrlToken().IsMatch(token).ShouldBeTrue();
        token.ShouldNotContain("=");
        token.ShouldNotContain("+");
        token.ShouldNotContain("/");
    }

    [Fact]
    public void Generate_never_repeats_within_a_thousand_calls()
    {
        var tokens = Enumerable.Range(0, 1_000).Select(_ => InviteToken.Generate()).ToList();

        tokens.Distinct().Count().ShouldBe(1_000);
    }

    [Fact]
    public void Hash_is_deterministic_64_lowercase_hex_characters_and_differs_from_its_input()
    {
        var token = InviteToken.Generate();

        var hash = InviteToken.Hash(token);

        hash.ShouldBe(InviteToken.Hash(token));
        hash.Length.ShouldBe(64);
        Regex.IsMatch(hash, "^[0-9a-f]{64}$").ShouldBeTrue();
        hash.ShouldNotBe(token);
    }

    [Fact]
    public void Hash_is_case_sensitive()
    {
        InviteToken.Hash("abc").ShouldNotBe(InviteToken.Hash("ABC"));
    }

    [Fact]
    public void Hash_matches_the_known_SHA256_vector_for_abc()
    {
        InviteToken.Hash("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(43, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [InlineData(5_000, false)]
    public void IsPlausible_bounds_the_token_length(int length, bool expected)
    {
        InviteToken.IsPlausible(new string('a', length)).ShouldBe(expected);
    }

    [Fact]
    public void IsPlausible_rejects_an_empty_token()
    {
        InviteToken.IsPlausible("").ShouldBeFalse();
    }
}
