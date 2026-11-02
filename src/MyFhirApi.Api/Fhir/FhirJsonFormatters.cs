using System.Text;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace MyFhirApi.Api.Fhir;

/// <summary>
/// FHIR concept: the official JSON media type is <c>application/fhir+json</c>.
/// FHIR JSON has its own rules (e.g. "resourceType" property, primitive extensions in "_field"),
/// so we use the Firely SDK serializer instead of plain System.Text.Json.
/// </summary>
public static class FhirMediaTypes
{
    public const string FhirJson = "application/fhir+json";
}

/// <summary>Reads a request body (application/fhir+json) into a Firely <see cref="Resource"/> POCO.</summary>
public sealed class FhirJsonInputFormatter : TextInputFormatter
{
    private static readonly FhirJsonDeserializer Deserializer = new();

    public FhirJsonInputFormatter()
    {
        SupportedMediaTypes.Add(FhirMediaTypes.FhirJson);
        SupportedMediaTypes.Add("application/json");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanReadType(Type type) => typeof(Resource).IsAssignableFrom(type);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        using var reader = new StreamReader(context.HttpContext.Request.Body, encoding);
        var json = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(json))
        {
            context.ModelState.AddModelError(context.ModelName, "Request body is empty; expected a FHIR resource.");
            return await InputFormatterResult.FailureAsync();
        }

        // TryDeserializeResource collects *all* problems (unknown elements, bad dates, wrong types...)
        // instead of stopping at the first one, which gives the client a useful OperationOutcome.
        if (!Deserializer.TryDeserializeResource(json, out var resource, out var issues) || resource is null)
        {
            foreach (var issue in issues)
                context.ModelState.AddModelError(context.ModelName, issue.Message);
            if (context.ModelState.IsValid)
                context.ModelState.AddModelError(context.ModelName, "Body is not a valid FHIR resource.");
            return await InputFormatterResult.FailureAsync();
        }

        return await InputFormatterResult.SuccessAsync(resource);
    }
}

/// <summary>Writes any Firely POCO (Resource, Bundle, OperationOutcome...) as application/fhir+json.</summary>
public sealed class FhirJsonOutputFormatter : TextOutputFormatter
{
    public FhirJsonOutputFormatter()
    {
        SupportedMediaTypes.Add(FhirMediaTypes.FhirJson);
        SupportedMediaTypes.Add("application/json");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanWriteType(Type? type) => type is not null && typeof(Base).IsAssignableFrom(type);

    public override async Task WriteResponseBodyAsync(OutputFormatterWriteContext context, Encoding selectedEncoding)
    {
        // Always answer with the FHIR media type, even if the client asked for application/json.
        context.HttpContext.Response.ContentType = $"{FhirMediaTypes.FhirJson}; charset=utf-8";

        // FHIR defines a "_pretty" query parameter for human-readable output.
        var pretty = context.HttpContext.Request.Query["_pretty"] == "true";
        var json = FhirJsonSerializer.Default.SerializeToString((Base)context.Object!, pretty, null);
        await context.HttpContext.Response.WriteAsync(json, selectedEncoding);
    }
}
