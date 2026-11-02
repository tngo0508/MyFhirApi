using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MyFhirApi.Api.Tests.Infrastructure;

/// <summary>Mints access tokens like an authorization server would, signed with a test-only key.</summary>
public static class TestTokens
{
    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("test-only-signing-key-that-is-long-enough-for-hs256"));

    public static string Create(string scopes, string? patientId = null)
    {
        var claims = new Dictionary<string, object> { ["scope"] = scopes, ["sub"] = "test-user" };
        if (patientId is not null) claims["patient"] = patientId;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
