using Hl7.Fhir.Model;

namespace MyFhirApi.Api.Auth;

/// <summary>
/// FHIR concept: a <b>compartment</b> is the set of resources that belong to one subject.
/// The Patient compartment for Patient/123 contains the Patient itself plus resources that point to it
/// (for an Observation, through <c>subject</c>). The full rules are at
/// https://hl7.org/fhir/R4/compartmentdefinition-patient.html
/// </summary>
public static class PatientCompartment
{
    public static bool Contains(Resource resource, string patientId) => resource switch
    {
        Patient p => p.Id == patientId,
        Observation o => o.Subject?.Reference == $"Patient/{patientId}",
        _ => false,
    };

    /// <summary>The extra search criterion that limits a search to one patient's compartment.</summary>
    public static (string Name, string Value) SearchCriterion(string resourceType, string patientId) => resourceType switch
    {
        "Patient" => ("_id", patientId),
        "Observation" => ("patient", $"Patient/{patientId}"),
        _ => throw new NotSupportedException($"No patient compartment rule for {resourceType}."),
    };
}
