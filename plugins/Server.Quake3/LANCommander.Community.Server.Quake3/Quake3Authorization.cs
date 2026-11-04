using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Components.Authorization;

namespace LANCommander.Community.Server.Quake3;

public sealed class Quake3Authorization(AuthenticationStateProvider authentication)
{
    public async Task RequireAdministratorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await authentication.GetAuthenticationStateAsync().WaitAsync(cancellationToken);
        if (!PluginAccessPolicy.Administrator.IsSatisfiedBy(state.User))
            throw new UnauthorizedAccessException("LANCommander administrator access is required.");
    }
}
