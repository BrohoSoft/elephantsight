using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Flarelytics.Api.Common;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// L'id dell'utente del token. Si chiama solo su rotte protette: lì un
    /// token senza <c>sub</c> non può arrivare, e se succede è un bug.
    /// </summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("Il token non contiene l'id dell'utente.");
}
