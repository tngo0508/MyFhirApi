using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using MyFhirApi.Api.Auth;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;
using MyFhirApi.Api.Search;

var builder = WebApplication.CreateBuilder(args);

// --- MVC + FHIR JSON --------------------------------------------------------------------------
builder.Services.AddControllers(options =>
{
    // Our FHIR formatters go first so Resource/Bundle/OperationOutcome use the Firely serializer.
    options.InputFormatters.Insert(0, new FhirJsonInputFormatter());
    options.OutputFormatters.Insert(0, new FhirJsonOutputFormatter());
    options.Filters.Add<FhirExceptionFilter>();
})
.ConfigureApiBehaviorOptions(options =>
{
    // We report invalid bodies ourselves, as OperationOutcome instead of ProblemDetails.
    options.SuppressModelStateInvalidFilter = true;
});

// --- Storage (SQL Server) ---------------------------------------------------------------------
// Default: SQL Server LocalDB (ships with Visual Studio). Change "ConnectionStrings:Fhir" to use another server.
builder.Services.AddDbContext<FhirDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<IConfiguration>().GetConnectionString("Fhir")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ResourceRepository>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<SampleDataLoader>();

// --- Security (SMART on FHIR) -----------------------------------------------------------------
// JwtBearer validates the access token. Its settings (Authority, audiences, signing keys) come from
// the "Authentication:Schemes:Bearer" config section; "dotnet user-jwts" fills that in for local testing.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false); // keep claim names like "scope" and "patient" as-is
builder.Services.Configure<SmartOptions>(builder.Configuration.GetSection(SmartOptions.Section));
builder.Services.AddSingleton<SmartAccessService>();

var app = builder.Build();

// --- Database setup ---------------------------------------------------------------------------
// EnsureCreated keeps the prototype simple. A real project would use EF Core migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FhirDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (app.Configuration.GetValue<bool>("SampleData:LoadOnStartup"))
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "SampleData");
        await scope.ServiceProvider.GetRequiredService<SampleDataLoader>().LoadIfEmptyAsync(folder);
    }
}

app.UseAuthentication();
app.MapControllers();

app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>.
public partial class Program;
