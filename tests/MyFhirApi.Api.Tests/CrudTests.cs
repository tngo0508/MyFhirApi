using System.Net;
using Hl7.Fhir.Model;
using MyFhirApi.Api.Tests.Infrastructure;
using static MyFhirApi.Api.Tests.Infrastructure.FhirHttp;

namespace MyFhirApi.Api.Tests;

/// <summary>The basic FHIR REST interactions: create, read, update, delete, history, vread, capabilities.</summary>
public sealed class CrudTests(FhirApiFactory factory) : IClassFixture<FhirApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Metadata_returns_a_capability_statement_listing_supported_resources()
    {
        var response = await _client.GetAsync("metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
        var statement = await response.ReadAsAsync<CapabilityStatement>();
        Assert.Equal(FHIRVersion.N4_0_1, statement.FhirVersion);
        Assert.Equal(["Patient", "Observation"], statement.Rest[0].Resource.Select(r => r.Type));
    }

    [Fact]
    public async Task Create_assigns_id_version_and_returns_location()
    {
        var input = NewPatient("Create");
        input.Id = "client-chosen-id"; // ignored by the server on create

        var response = await _client.PostResourceAsync(input);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsAsync<Patient>();
        Assert.NotEqual("client-chosen-id", created.Id);
        Assert.Equal("1", created.Meta.VersionId);
        Assert.NotNull(created.Meta.LastUpdated);
        Assert.Equal("W/\"1\"", response.Headers.ETag?.ToString());
        Assert.EndsWith($"/Patient/{created.Id}/_history/1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Read_returns_the_stored_resource()
    {
        var created = await _client.CreateAsync(NewPatient("Read", "Rita"));

        var response = await _client.GetAsync($"Patient/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = await response.ReadAsAsync<Patient>();
        Assert.Equal("Rita", read.Name[0].Given.Single());
    }

    [Fact]
    public async Task Update_creates_a_new_version_and_rejects_stale_if_match()
    {
        var patient = await _client.CreateAsync(NewPatient("Update"));
        patient.Name[0].Family = "Updated";

        var ok = await _client.PutResourceAsync(patient, ifMatch: "W/\"1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("2", (await ok.ReadAsAsync<Patient>()).Meta.VersionId);

        // Another client still thinks version 1 is current -> 412 instead of silently overwriting.
        var stale = await _client.PutResourceAsync(patient, ifMatch: "W/\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(OperationOutcome.IssueType.Conflict, (await stale.ReadAsAsync<OperationOutcome>()).Issue[0].Code);
    }

    [Fact]
    public async Task Put_to_an_unknown_id_creates_the_resource()
    {
        var patient = NewPatient("UpdateAsCreate");
        patient.Id = $"chosen-{Guid.NewGuid():N}";

        var response = await _client.PutResourceAsync(patient);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"Patient/{patient.Id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_keeps_history_and_read_returns_gone()
    {
        var patient = await _client.CreateAsync(NewPatient("Delete"));
        patient.Gender = AdministrativeGender.Female;
        await _client.PutResourceAsync(patient);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"Patient/{patient.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"Patient/{patient.Id}")).StatusCode);

        var history = await (await _client.GetAsync($"Patient/{patient.Id}/_history")).ReadAsAsync<Bundle>();
        Assert.Equal(Bundle.BundleType.History, history.Type);
        Assert.Equal([Bundle.HTTPVerb.DELETE, Bundle.HTTPVerb.PUT, Bundle.HTTPVerb.POST],
            history.Entry.Select(e => e.Request.Method!.Value));

        var v1 = await _client.GetAsync($"Patient/{patient.Id}/_history/1");
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Equal(AdministrativeGender.Unknown, (await v1.ReadAsAsync<Patient>()).Gender); // the value before the update
    }

    [Fact]
    public async Task Invalid_json_returns_400_with_operation_outcome()
    {
        var body = new StringContent("""{"resourceType":"Patient","birthDate":"not-a-date"}""",
            System.Text.Encoding.UTF8, "application/fhir+json");

        var response = await _client.PostAsync("Patient", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var outcome = await response.ReadAsAsync<OperationOutcome>();
        Assert.Contains(outcome.Issue, i => i.Severity == OperationOutcome.IssueSeverity.Error);
    }

    [Fact]
    public async Task Body_type_must_match_url_type()
    {
        var response = await _client.PostAsync("Observation", Content(NewPatient("Mismatch")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Observation_must_reference_an_existing_patient()
    {
        var response = await _client.PostResourceAsync(NewObservation("does-not-exist"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var outcome = await response.ReadAsAsync<OperationOutcome>();
        Assert.Contains("does not exist", outcome.Issue[0].Diagnostics);
    }

    [Fact]
    public async Task Unsupported_resource_type_returns_404()
    {
        var response = await _client.GetAsync("Encounter/123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OperationOutcome.IssueType.NotFound, (await response.ReadAsAsync<OperationOutcome>()).Issue[0].Code);
    }
}
