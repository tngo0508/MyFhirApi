using System.Net;
using Hl7.Fhir.Model;
using MyFhirApi.Api.Auth;
using MyFhirApi.Api.Tests.Infrastructure;
using static MyFhirApi.Api.Tests.Infrastructure.FhirHttp;

namespace MyFhirApi.Api.Tests;

/// <summary>SMART on FHIR scope enforcement, with Smart:Enabled=true.</summary>
public sealed class SmartAuthTests(SecuredFhirApiFactory factory) : IClassFixture<SecuredFhirApiFactory>
{
    private HttpClient Client(string? scopes = null, string? patientId = null)
    {
        var client = factory.CreateClient();
        return scopes is null ? client : client.WithToken(TestTokens.Create(scopes, patientId));
    }

    [Fact]
    public async Task Metadata_and_smart_configuration_are_public()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("metadata")).StatusCode);
        var smart = await Client().GetAsync(".well-known/smart-configuration");
        Assert.Equal(HttpStatusCode.OK, smart.StatusCode);
        Assert.Contains("token_endpoint", await smart.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_token_returns_401_with_operation_outcome()
    {
        var response = await Client().GetAsync("Patient");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(OperationOutcome.IssueType.Login, (await response.ReadAsAsync<OperationOutcome>()).Issue[0].Code);
    }

    [Fact]
    public async Task Read_only_scope_cannot_create()
    {
        var response = await Client("user/Patient.read").PostResourceAsync(NewPatient("NoWrite"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, """{"resourceType":"Patient","birthDate":"yesterday"}""")]
    [InlineData("user/Patient.read", HttpStatusCode.Forbidden, """{"resourceType":"Patient","birthDate":"yesterday"}""")]
    [InlineData("user/Patient.read", HttpStatusCode.Forbidden, "{not json")]
    public async Task Permissions_are_checked_before_the_body_is_validated(string? scopes, HttpStatusCode expected, string body)
    {
        var invalidBody = new StringContent(body, System.Text.Encoding.UTF8, "application/fhir+json");

        var response = await Client(scopes).PostAsync("Patient", invalidBody);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Scope_for_another_resource_type_is_forbidden()
    {
        var response = await Client("user/Patient.rs").GetAsync("Observation");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task User_scope_with_write_permission_can_create_and_read()
    {
        var client = Client("user/*.cruds");

        var patient = await client.CreateAsync(NewPatient("Writer"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"Patient/{patient.Id}")).StatusCode);
    }

    [Fact]
    public async Task Patient_scope_only_sees_that_patients_data()
    {
        var admin = Client("system/*.*");
        var me = await admin.CreateAsync(NewPatient("Me"));
        var other = await admin.CreateAsync(NewPatient("Other"));
        await admin.CreateAsync(NewObservation(me.Id));
        await admin.CreateAsync(NewObservation(other.Id));

        var patientApp = Client("patient/*.rs", patientId: me.Id);

        // Search is narrowed to the patient in the token.
        var observations = await (await patientApp.GetAsync("Observation")).ReadAsAsync<Bundle>();
        Assert.Equal(1, observations.Total);
        Assert.Equal($"Patient/{me.Id}", ((Observation)observations.Entry[0].Resource).Subject.Reference);

        Assert.Equal(HttpStatusCode.OK, (await patientApp.GetAsync($"Patient/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await patientApp.GetAsync($"Patient/{other.Id}")).StatusCode);
    }

    [Fact]
    public async Task Patient_scope_without_patient_claim_is_forbidden()
    {
        var response = await Client("patient/*.rs").GetAsync("Observation");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

/// <summary>Unit tests for parsing SMART scope strings.</summary>
public sealed class SmartScopeTests
{
    [Theory]
    [InlineData("patient/Observation.read", "Observation", FhirInteraction.Read, true)]
    [InlineData("patient/Observation.read", "Observation", FhirInteraction.Search, true)]
    [InlineData("patient/Observation.read", "Observation", FhirInteraction.Create, false)]
    [InlineData("user/*.write", "Patient", FhirInteraction.Update, true)]
    [InlineData("user/*.write", "Patient", FhirInteraction.Read, false)]
    [InlineData("system/*.*", "Patient", FhirInteraction.Delete, true)]
    [InlineData("user/Patient.rs", "Patient", FhirInteraction.Search, true)]
    [InlineData("user/Patient.rs", "Patient", FhirInteraction.Update, false)]
    [InlineData("user/Patient.rs", "Observation", FhirInteraction.Read, false)]
    [InlineData("patient/Observation.rs?category=laboratory", "Observation", FhirInteraction.Read, true)]
    public void Scope_allows_expected_interactions(string raw, string type, FhirInteraction interaction, bool expected)
    {
        Assert.True(SmartScope.TryParse(raw, out var scope));
        Assert.Equal(expected, scope.Allows(type, interaction));
    }

    [Theory]
    [InlineData("openid")]
    [InlineData("launch/patient")]
    [InlineData("admin/Patient.read")]
    [InlineData("user/Patient.xyz")]
    public void Non_resource_scopes_are_not_parsed(string raw) => Assert.False(SmartScope.TryParse(raw, out _));
}
