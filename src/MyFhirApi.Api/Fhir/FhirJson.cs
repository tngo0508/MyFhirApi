using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace MyFhirApi.Api.Fhir;

/// <summary>Small wrapper around the Firely SDK JSON serializer, used for database storage.</summary>
public static class FhirJson
{
    private static readonly FhirJsonDeserializer Deserializer = new();

    public static string Serialize(Base value) => FhirJsonSerializer.Default.SerializeToString(value, false, null);

    public static Resource Parse(string json) => Deserializer.DeserializeResource(json);

    public static T Parse<T>(string json) where T : Resource => (T)Parse(json);
}
