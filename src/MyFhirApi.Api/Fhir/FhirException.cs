using System.Net;
using Hl7.Fhir.Model;

namespace MyFhirApi.Api.Fhir;

/// <summary>
/// Thrown anywhere in the app to stop processing and return a FHIR error.
/// <see cref="FhirExceptionFilter"/> turns it into an <see cref="OperationOutcome"/> response.
///
/// FHIR concept: errors are never plain text or ProblemDetails. They are an OperationOutcome
/// resource that lists one or more "issues" (severity + code + human-readable text).
/// </summary>
public sealed class FhirException(
    HttpStatusCode status,
    string message,
    OperationOutcome.IssueType issueType = OperationOutcome.IssueType.Processing)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public OperationOutcome.IssueType IssueType { get; } = issueType;

    public static FhirException BadRequest(string message) =>
        new(HttpStatusCode.BadRequest, message, OperationOutcome.IssueType.Invalid);

    public static FhirException NotFound(string message) =>
        new(HttpStatusCode.NotFound, message, OperationOutcome.IssueType.NotFound);

    public static FhirException Gone(string message) =>
        new(HttpStatusCode.Gone, message, OperationOutcome.IssueType.Deleted);

    public static FhirException PreconditionFailed(string message) =>
        new(HttpStatusCode.PreconditionFailed, message, OperationOutcome.IssueType.Conflict);

    public static FhirException Unauthorized(string message) =>
        new(HttpStatusCode.Unauthorized, message, OperationOutcome.IssueType.Login);

    public static FhirException Forbidden(string message) =>
        new(HttpStatusCode.Forbidden, message, OperationOutcome.IssueType.Forbidden);
}
