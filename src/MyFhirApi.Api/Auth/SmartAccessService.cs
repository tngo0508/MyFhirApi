using System.Security.Claims;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using MyFhirApi.Api.Fhir;

namespace MyFhirApi.Api.Auth;

public sealed class SmartOptions
{
    public const string Section = "Smart";

    /// <summary>When false, every request is allowed. Handy while learning; never do this in production.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The OAuth2 endpoints of your authorization server (Entra ID, Keycloak, Okta...).
    /// This API only <i>validates</i> tokens; issuing them is the authorization server's job.
    /// </summary>
    public string AuthorizeEndpoint { get; set; } = "https://example.org/oauth2/authorize";
    public string TokenEndpoint { get; set; } = "https://example.org/oauth2/token";
}

/// <summary>
/// The result of an access check.
/// <see cref="PatientId"/> is set when access came from a <c>patient/</c> scope: the caller may only
/// touch data in that patient's <b>compartment</b> (the Patient itself and resources about them).
/// </summary>
public sealed record AccessDecision(string? PatientId)
{
    public static readonly AccessDecision Unrestricted = new((string?)null);

    /// <summary>Throws 403 if <paramref name="resource"/> is outside the allowed patient compartment.</summary>
    public void EnsureCanAccess(Resource resource)
    {
        if (PatientId is not null && !PatientCompartment.Contains(resource, PatientId))
            throw FhirException.Forbidden($"Access to {resource.TypeName}/{resource.Id} is outside the patient context.");
    }
}

/// <summary>
/// Checks the SMART scopes in the caller's access token before each FHIR interaction.
/// Token <i>validation</i> (signature, expiry, audience) is done earlier by ASP.NET Core's JwtBearer handler.
/// </summary>
public sealed class SmartAccessService(IOptions<SmartOptions> options)
{
    public AccessDecision Authorize(ClaimsPrincipal user, string resourceType, FhirInteraction interaction)
    {
        if (!options.Value.Enabled) return AccessDecision.Unrestricted;

        if (user.Identity?.IsAuthenticated != true)
            throw FhirException.Unauthorized("A valid bearer token is required.");

        var granting = GetScopes(user).Where(s => s.Allows(resourceType, interaction)).ToList();
        if (granting.Count == 0)
            throw FhirException.Forbidden($"No scope grants '{interaction}' on {resourceType}.");

        // A user/ or system/ scope is not limited to one patient.
        if (granting.Any(s => s.Context != SmartScope.Contexts.Patient))
            return AccessDecision.Unrestricted;

        // Only patient/ scopes matched, so the token must say which patient it is for.
        var patientId = user.FindFirst("patient")?.Value;
        if (string.IsNullOrEmpty(patientId))
            throw FhirException.Forbidden("patient/ scopes require a 'patient' claim in the token.");
        return new AccessDecision(patientId);
    }

    /// <summary>
    /// The "scope" claim is a space-separated string in most tokens. Some issuers (including
    /// <c>dotnet user-jwts</c>) send one claim per scope instead, so we accept both.
    /// </summary>
    public static IEnumerable<SmartScope> GetScopes(ClaimsPrincipal user) =>
        user.FindAll(c => c.Type is "scope" or "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(raw => SmartScope.TryParse(raw, out var scope) ? scope : null)
            .OfType<SmartScope>();
}
