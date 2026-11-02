using Hl7.Fhir.Model;
using MyFhirApi.Api.Tests.Infrastructure;
using static MyFhirApi.Api.Tests.Infrastructure.FhirHttp;

namespace MyFhirApi.Api.Tests;

/// <summary>
/// FHIR search behavior per parameter type. Each test uses its own unique family name or code,
/// so tests sharing the database don't see each other's data.
/// </summary>
public sealed class SearchTests(FhirApiFactory factory) : IClassFixture<FhirApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Bundle> SearchAsync(string query)
    {
        var response = await _client.GetAsync(query);
        response.EnsureSuccessStatusCode();
        return await response.ReadAsAsync<Bundle>();
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private static IEnumerable<T> Matches<T>(Bundle bundle) =>
        bundle.Entry.Where(e => e.Search?.Mode == Bundle.SearchEntryMode.Match).Select(e => e.Resource).OfType<T>();

    [Fact]
    public async Task String_search_is_case_insensitive_prefix_by_default()
    {
        var family = Unique("Prefix");
        await _client.CreateAsync(NewPatient(family));

        var bundle = await SearchAsync($"Patient?family={family[..10].ToLowerInvariant()}");

        Assert.Equal(Bundle.BundleType.Searchset, bundle.Type);
        Assert.Equal(1, bundle.Total);
        Assert.Equal(family, Matches<Patient>(bundle).Single().Name[0].Family);
    }

    [Fact]
    public async Task String_exact_and_contains_modifiers()
    {
        var family = Unique("Mod");
        await _client.CreateAsync(NewPatient(family));

        Assert.Equal(1, (await SearchAsync($"Patient?family:exact={family}")).Total);
        Assert.Equal(0, (await SearchAsync($"Patient?family:exact={family.ToUpperInvariant()}")).Total);
        Assert.Equal(1, (await SearchAsync($"Patient?family:contains={family[5..12]}")).Total);
    }

    [Fact]
    public async Task Comma_means_or_and_repeated_parameters_mean_and()
    {
        var a = Unique("OrA");
        var b = Unique("OrB");
        await _client.CreateAsync(NewPatient(a, gender: AdministrativeGender.Female));
        await _client.CreateAsync(NewPatient(b, gender: AdministrativeGender.Male));

        Assert.Equal(2, (await SearchAsync($"Patient?family={a},{b}")).Total);
        Assert.Equal(1, (await SearchAsync($"Patient?family={a},{b}&gender=male")).Total);
    }

    [Fact]
    public async Task Token_search_on_identifier_with_system()
    {
        var mrn = Unique("MRN");
        var patient = NewPatient("Token");
        patient.Identifier.Add(new Identifier("http://example.org/mrn", mrn));
        await _client.CreateAsync(patient);

        Assert.Equal(1, (await SearchAsync($"Patient?identifier=http://example.org/mrn|{mrn}")).Total);
        Assert.Equal(1, (await SearchAsync($"Patient?identifier={mrn}")).Total);
        Assert.Equal(0, (await SearchAsync($"Patient?identifier=http://other.org|{mrn}")).Total);
    }

    [Fact]
    public async Task Date_search_supports_precision_and_prefixes()
    {
        var family = Unique("Date");
        await _client.CreateAsync(NewPatient(family, birthDate: "1975-06-15"));

        Assert.Equal(1, (await SearchAsync($"Patient?family={family}&birthdate=1975")).Total);       // same year
        Assert.Equal(1, (await SearchAsync($"Patient?family={family}&birthdate=1975-06")).Total);    // same month
        Assert.Equal(0, (await SearchAsync($"Patient?family={family}&birthdate=1975-07")).Total);
        Assert.Equal(1, (await SearchAsync($"Patient?family={family}&birthdate=ge1975-01-01")).Total);
        Assert.Equal(0, (await SearchAsync($"Patient?family={family}&birthdate=gt1975-12-31")).Total);
        Assert.Equal(1, (await SearchAsync($"Patient?family={family}&birthdate=lt1980")).Total);
    }

    [Fact]
    public async Task Reference_search_and_include_return_observations_with_their_patient()
    {
        var patient = await _client.CreateAsync(NewPatient(Unique("Ref")));
        await _client.CreateAsync(NewObservation(patient.Id, "4548-4"));
        await _client.CreateAsync(NewObservation(patient.Id, "2339-0"));

        var byPatient = await SearchAsync($"Observation?patient={patient.Id}");
        Assert.Equal(2, byPatient.Total);

        var withInclude = await SearchAsync($"Observation?subject=Patient/{patient.Id}&code=http://loinc.org|4548-4&_include=Observation:subject");
        Assert.Equal(1, withInclude.Total);
        var included = withInclude.Entry.Single(e => e.Search.Mode == Bundle.SearchEntryMode.Include);
        Assert.Equal(patient.Id, included.Resource.Id);
    }

    [Fact]
    public async Task Paging_returns_next_and_previous_links()
    {
        var family = Unique("Page");
        for (var i = 0; i < 5; i++) await _client.CreateAsync(NewPatient(family, $"Given{i}"));

        var first = await SearchAsync($"Patient?family={family}&_count=2");
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Entry.Count);
        Assert.Null(first.PreviousLink);
        Assert.NotNull(first.NextLink);

        var second = await SearchAsync(first.NextLink.ToString());
        Assert.Equal(2, second.Entry.Count);
        Assert.NotNull(second.PreviousLink);
        Assert.Empty(Matches<Patient>(first).Select(p => p.Id).Intersect(Matches<Patient>(second).Select(p => p.Id)));
    }

    [Fact]
    public async Task Deleted_resources_are_not_found_by_search()
    {
        var family = Unique("Gone");
        var patient = await _client.CreateAsync(NewPatient(family));
        await _client.DeleteAsync($"Patient/{patient.Id}");

        Assert.Equal(0, (await SearchAsync($"Patient?family={family}")).Total);
    }

    [Fact]
    public async Task Unknown_parameters_are_ignored_with_a_warning()
    {
        var bundle = await SearchAsync("Patient?shoe-size=42&_count=1");

        var outcome = bundle.Entry.Single(e => e.Search.Mode == Bundle.SearchEntryMode.Outcome).Resource as OperationOutcome;
        Assert.Contains("shoe-size", outcome!.Issue[0].Diagnostics);
    }
}
