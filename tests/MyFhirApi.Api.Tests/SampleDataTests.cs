using Hl7.Fhir.Model;
using Microsoft.Extensions.DependencyInjection;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;
using MyFhirApi.Api.Tests.Infrastructure;

namespace MyFhirApi.Api.Tests;

public sealed class SampleDataTests(FhirApiFactory factory) : IClassFixture<FhirApiFactory>
{
    [Fact]
    public async Task Bundled_sample_file_loads_and_urn_references_are_resolved()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SampleData", "synthetic-patients.json");
        var bundle = FhirJson.Parse<Bundle>(await File.ReadAllTextAsync(path));

        using var scope = factory.Services.CreateScope();
        var (loaded, skipped) = await scope.ServiceProvider.GetRequiredService<SampleDataLoader>().LoadBundleAsync(bundle);

        Assert.Equal(11, loaded);  // 3 Patients + 8 Observations
        Assert.Equal(1, skipped);  // the Encounter (not supported by this server)

        var client = factory.CreateClient();
        var search = await (await client.GetAsync("Observation?patient=5f2a7c1e-3b64-4c9a-9f4e-1a2b3c4d5e02")).ReadAsAsync<Bundle>();
        Assert.Equal(3, search.Total);
        Assert.All(search.Entry.Select(e => (Observation)e.Resource),
            o => Assert.Equal("Patient/5f2a7c1e-3b64-4c9a-9f4e-1a2b3c4d5e02", o.Subject.Reference));
    }
}
