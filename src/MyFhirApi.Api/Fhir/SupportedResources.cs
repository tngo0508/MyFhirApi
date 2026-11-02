using Hl7.Fhir.Model;
using MyFhirApi.Api.Search;

namespace MyFhirApi.Api.Fhir;

/// <summary>
/// What one resource type supports on this server.
/// </summary>
/// <param name="TypeName">The FHIR resource type, e.g. <c>Patient</c>.</param>
/// <param name="PocoType">The Firely SDK class for this type.</param>
/// <param name="SearchParameters">The search parameters this server indexes for the type.</param>
/// <param name="Includes">
/// Reference search parameters that <c>_include</c> may follow, e.g. <c>Observation:subject</c>.
/// </param>
public sealed record ResourceDefinition(
    string TypeName,
    Type PocoType,
    IReadOnlyList<SearchParameterDefinition> SearchParameters,
    IReadOnlyList<string> Includes)
{
    public SearchParameterDefinition? FindParameter(string name) =>
        SearchParameters.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// <b>The one place that lists which FHIR resources this server supports.</b>
///
/// To support a new resource type (e.g. Encounter), add an entry here. The controllers, search indexer
/// and CapabilityStatement all read from this list. The FHIRPath expressions are copied from the
/// official R4 SearchParameter definitions (https://hl7.org/fhir/R4/searchparameter-registry.html).
/// </summary>
public static class SupportedResources
{
    public static readonly IReadOnlyDictionary<string, ResourceDefinition> All =
        new Dictionary<string, ResourceDefinition>
        {
            ["Patient"] = new(
                "Patient",
                typeof(Patient),
                [
                    new("name", SearchParamKind.String, "Patient.name"),
                    new("family", SearchParamKind.String, "Patient.name.family"),
                    new("given", SearchParamKind.String, "Patient.name.given"),
                    new("identifier", SearchParamKind.Token, "Patient.identifier"),
                    new("gender", SearchParamKind.Token, "Patient.gender"),
                    new("birthdate", SearchParamKind.Date, "Patient.birthDate"),
                ],
                Includes: []),

            ["Observation"] = new(
                "Observation",
                typeof(Observation),
                [
                    new("subject", SearchParamKind.Reference, "Observation.subject"),
                    // "patient" is "subject, but only when the subject is a Patient".
                    new("patient", SearchParamKind.Reference, "Observation.subject", TargetType: "Patient"),
                    new("code", SearchParamKind.Token, "Observation.code"),
                    new("status", SearchParamKind.Token, "Observation.status"),
                    new("category", SearchParamKind.Token, "Observation.category"),
                    new("date", SearchParamKind.Date, "Observation.effective"),
                ],
                Includes: ["subject", "patient"]),
        };

    public static ResourceDefinition Get(string typeName) =>
        All.TryGetValue(typeName, out var def)
            ? def
            : throw FhirException.NotFound($"Resource type '{typeName}' is not supported by this server.");
}
