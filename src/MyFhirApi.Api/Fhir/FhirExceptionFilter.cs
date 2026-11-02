using System.Net;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MyFhirApi.Api.Fhir;

/// <summary>
/// Converts exceptions into OperationOutcome responses, so every error from this server is a valid FHIR resource.
/// </summary>
public sealed class FhirExceptionFilter(ILogger<FhirExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is FhirException fe)
        {
            context.Result = Outcome(fe.Status, OperationOutcomeFactory.Error(fe.IssueType, fe.Message));
            if (fe.Status == HttpStatusCode.Unauthorized)
                context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
        }
        else
        {
            logger.LogError(context.Exception, "Unhandled error");
            context.Result = Outcome(HttpStatusCode.InternalServerError,
                OperationOutcomeFactory.Error(OperationOutcome.IssueType.Exception, "An unexpected error occurred."));
        }
        context.ExceptionHandled = true;
    }

    private static ObjectResult Outcome(HttpStatusCode status, OperationOutcome outcome) =>
        new(outcome) { StatusCode = (int)status };
}
