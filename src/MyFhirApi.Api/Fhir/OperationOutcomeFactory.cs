using Hl7.Fhir.Model;

namespace MyFhirApi.Api.Fhir;

public static class OperationOutcomeFactory
{
    public static OperationOutcome Create(
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        params string[] messages) => new()
    {
        Issue = messages.Select(m => new OperationOutcome.IssueComponent
        {
            Severity = severity,
            Code = code,
            Diagnostics = m,
        }).ToList(),
    };

    public static OperationOutcome Error(OperationOutcome.IssueType code, params string[] messages) =>
        Create(OperationOutcome.IssueSeverity.Error, code, messages);
}
