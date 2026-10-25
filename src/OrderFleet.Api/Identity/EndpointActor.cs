using System.Security.Claims;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Identity;

internal static class EndpointActor
{
    internal static Actor ToActor(this ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirst("sub")!.Value),
            principal.FindAll("role").Select(x => x.Value).ToHashSet(StringComparer.Ordinal));
}
