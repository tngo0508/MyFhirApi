using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyFhirApi.Api.Auth;

namespace MyFhirApi.Api.Controllers;

/// <summary>
/// SMART on FHIR discovery: apps fetch <c>/.well-known/smart-configuration</c> to find the OAuth2 endpoints
/// and the SMART features this server supports. This is plain JSON, not a FHIR resource.
/// https://hl7.org/fhir/smart-app-launch/conformance.html
/// </summary>
[ApiController]
public sealed class SmartConfigController(IOptions<SmartOptions> options) : ControllerBase
{
    [HttpGet(".well-known/smart-configuration")]
    [Produces("application/json")]
    public IActionResult Get() => Ok(new
    {
        authorization_endpoint = options.Value.AuthorizeEndpoint,
        token_endpoint = options.Value.TokenEndpoint,
        grant_types_supported = new[] { "authorization_code", "client_credentials" },
        scopes_supported = new[]
        {
            "openid", "fhirUser", "launch/patient", "offline_access",
            "patient/*.rs", "user/*.cruds", "system/*.rs",
            "patient/*.read", "user/*.*", "system/*.read",
        },
        response_types_supported = new[] { "code" },
        code_challenge_methods_supported = new[] { "S256" },
        capabilities = new[]
        {
            "launch-standalone", "client-public", "client-confidential-symmetric",
            "context-standalone-patient", "permission-patient", "permission-user", "permission-v1", "permission-v2",
        },
    });
}
