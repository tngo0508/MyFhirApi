using System.Net.Http.Headers;
using System.Text;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace MyFhirApi.Api.Tests.Infrastructure;

/// <summary>Helpers to send and read FHIR JSON with the Firely SDK.</summary>
public static class FhirHttp
{
    private static readonly FhirJsonDeserializer Deserializer = new();

    public static StringContent Content(Resource resource) =>
        new(FhirJsonSerializer.Default.SerializeToString(resource, false, null), Encoding.UTF8, "application/fhir+json");

    public static Task<HttpResponseMessage> PostResourceAsync(this HttpClient client, Resource resource) =>
        client.PostAsync(resource.TypeName, Content(resource));

    public static Task<HttpResponseMessage> PutResourceAsync(this HttpClient client, Resource resource, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{resource.TypeName}/{resource.Id}") { Content = Content(resource) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    public static async Task<T> ReadAsAsync<T>(this HttpResponseMessage response) where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync();
        return (T)Deserializer.DeserializeResource(json);
    }

    /// <summary>Creates a resource and returns what the server stored (with id and meta filled in).</summary>
    public static async Task<T> CreateAsync<T>(this HttpClient client, T resource) where T : Resource
    {
        var response = await client.PostResourceAsync(resource);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsAsync<T>();
    }

    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static Patient NewPatient(string family, string given = "Test", string? birthDate = "1990-01-01",
        AdministrativeGender gender = AdministrativeGender.Unknown) => new()
    {
        Name = [new HumanName { Family = family, Given = [given] }],
        BirthDate = birthDate,
        Gender = gender,
    };

    public static Observation NewObservation(string patientId, string loincCode = "8867-4", string effective = "2026-01-15T10:00:00Z") => new()
    {
        Status = ObservationStatus.Final,
        Code = new CodeableConcept("http://loinc.org", loincCode),
        Subject = new ResourceReference($"Patient/{patientId}"),
        Effective = new FhirDateTime(effective),
        Value = new Quantity(72, "/min"),
    };
}
