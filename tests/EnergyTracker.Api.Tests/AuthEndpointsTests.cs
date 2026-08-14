using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class AuthEndpointsTests
{
    [Theory]
    [InlineData("/join/abc123")]
    [InlineData("/")]
    [InlineData("/some/nested/path")]
    public void Accepts_a_legitimate_same_origin_relative_path(string returnUrl)
    {
        AuthEndpoints.IsSafeLocalReturnUrl(returnUrl).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData("//evil.example/join/abc123")]
    [InlineData("https://evil.example")]
    [InlineData("https://evil.example/join/abc123")]
    [InlineData("/\\evil.example")]
    [InlineData("join/abc123")]
    [InlineData("http://evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\r/evil.example")]
    [InlineData("/\n/evil.example")]
    public void Rejects_anything_that_could_redirect_off_origin(string? returnUrl)
    {
        AuthEndpoints.IsSafeLocalReturnUrl(returnUrl).ShouldBeFalse();
    }
}
