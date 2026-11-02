using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MyFhirApi.Api.Data;

namespace MyFhirApi.Api.Tests.Infrastructure;

/// <summary>
/// Runs the real API in memory against a throwaway SQL Server LocalDB database,
/// which is dropped when the test class finishes.
/// </summary>
public class FhirApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"MyFhirApi_Tests_{Guid.NewGuid():N}";

    protected virtual bool SmartEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Fhir"] =
                $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True",
            ["Smart:Enabled"] = SmartEnabled.ToString(),
            ["SampleData:LoadOnStartup"] = "false",
        }));

        // Trust tokens signed with the test key (see TestTokens) instead of a real authorization server.
        builder.ConfigureServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    IssuerSigningKey = TestTokens.SigningKey,
                };
            }));
    }

    Task IAsyncLifetime.InitializeAsync()
    {
        _ = Server; // starts the app, which creates the database
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        using (var scope = Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FhirDbContext>().Database.EnsureDeletedAsync();
        await DisposeAsync();
    }
}

/// <summary>Same API, but with SMART scope checks switched on.</summary>
public sealed class SecuredFhirApiFactory : FhirApiFactory
{
    protected override bool SmartEnabled => true;
}
