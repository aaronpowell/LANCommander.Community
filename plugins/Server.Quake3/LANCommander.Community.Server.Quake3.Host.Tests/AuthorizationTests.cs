using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("Administrator", false)]
    [InlineData("Player", true)]
    public async Task RejectsAnonymousAndNonAdministrators(string? role, bool authenticated)
    {
        var auth = new Quake3Authorization(new TestAuthentication(role, authenticated));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            auth.RequireAdministratorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RechecksAuthenticationForEveryOperation()
    {
        var authentication = new TestAuthentication("Administrator", true);
        var auth = new Quake3Authorization(authentication);
        await auth.RequireAdministratorAsync(CancellationToken.None);
        authentication.User = new ClaimsPrincipal(new ClaimsIdentity());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            auth.RequireAdministratorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UsesTheHostsCaseInsensitiveAdministratorPolicy()
    {
        var auth = new Quake3Authorization(new TestAuthentication("aDmInIsTrAtOr", true));
        await auth.RequireAdministratorAsync(CancellationToken.None);
    }
}

internal sealed class TestAuthentication(string? role, bool authenticated) : AuthenticationStateProvider
{
    public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity(
        role is null ? [] : [new Claim(ClaimTypes.Role, role)], authenticated ? "test" : null));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(User));
}
