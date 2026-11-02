using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyFhirApi.Api.Auth;
using MyFhirApi.Api.Fhir;
using MyFhirApi.Api.Search;
using static Hl7.Fhir.Model.CapabilityStatement;

namespace MyFhirApi.Api.Controllers;

/// <summary>
/// FHIR concept: <c>GET /metadata</c> returns a <b>CapabilityStatement</b>, the server's machine-readable
/// "contract": FHIR version, supported resources, interactions, search parameters and security.
/// FHIR clients read it to find out what the server can do. It plays the role OpenAPI/Swagger plays elsewhere.
/// </summary>
[ApiController]
public sealed class MetadataController(IOptions<SmartOptions> smartOptions) : ControllerBase
{
    [HttpGet("metadata")]
    public IActionResult Get()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var statement = new CapabilityStatement
        {
            Status = PublicationStatus.Active,
            Date = "2026-11-01",
            Kind = CapabilityStatementKind.Instance,
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["json"],
            Software = new SoftwareComponent { Name = "MyFhirApi (learning prototype)", Version = "0.1.0" },
            Implementation = new ImplementationComponent { Description = "MyFhirApi sample FHIR R4 server", Url = baseUrl },
            Rest =
            [
                new RestComponent
                {
                    Mode = RestfulCapabilityMode.Server,
                    Security = Security(),
                    Resource = SupportedResources.All.Values.Select(Describe).ToList(),
                },
            ],
        };
        return Ok(statement);
    }

    private static ResourceComponent Describe(ResourceDefinition def) => new()
    {
        Type = def.TypeName,
        Versioning = ResourceVersionPolicy.Versioned,
        ReadHistory = true,
        UpdateCreate = true,
        Interaction = new[]
        {
            TypeRestfulInteraction.Create, TypeRestfulInteraction.Read, TypeRestfulInteraction.Vread,
            TypeRestfulInteraction.Update, TypeRestfulInteraction.Delete, TypeRestfulInteraction.HistoryInstance,
            TypeRestfulInteraction.SearchType,
        }.Select(code => new ResourceInteractionComponent { Code = code }).ToList(),
        SearchInclude = def.Includes.Select(i => $"{def.TypeName}:{i}").ToList(),
        SearchParam = def.SearchParameters
            .Select(p => new CapabilityStatement.SearchParamComponent
            {
                Name = p.Name,
                // Link to the official definition so clients know exactly what the parameter means.
                Definition = $"http://hl7.org/fhir/SearchParameter/{Official(def.TypeName, p.Name)}",
                Type = p.Type switch
                {
                    SearchParamKind.String => SearchParamType.String,
                    SearchParamKind.Token => SearchParamType.Token,
                    SearchParamKind.Date => SearchParamType.Date,
                    _ => SearchParamType.Reference,
                },
            })
            .Prepend(new CapabilityStatement.SearchParamComponent
            {
                Name = "_id",
                Definition = "http://hl7.org/fhir/SearchParameter/Resource-id",
                Type = SearchParamType.Token,
            })
            .ToList(),
    };

    // Some R4 parameters are defined on several resources at once, so their ids are "clinical-...", "individual-..." etc.
    private static string Official(string type, string name) => (type, name) switch
    {
        ("Patient", "family" or "given" or "gender" or "birthdate") => $"individual-{name}",
        ("Observation", "code" or "date" or "patient") => $"clinical-{name}",
        _ => $"{type}-{name}",
    };

    /// <summary>Tells SMART clients that this server uses OAuth2 and where to get tokens.</summary>
    private SecurityComponent Security()
    {
        var options = smartOptions.Value;
        var security = new SecurityComponent
        {
            Cors = true,
            Service = [new CodeableConcept("http://terminology.hl7.org/CodeSystem/restful-security-service", "SMART-on-FHIR")],
            Description = options.Enabled
                ? "SMART on FHIR: send an OAuth2 bearer token with SMART scopes."
                : "Security is DISABLED (Smart:Enabled=false). For local learning only.",
        };
        security.Extension.Add(new Extension
        {
            Url = "http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris",
            Extension =
            [
                new Extension("authorize", new FhirUri(options.AuthorizeEndpoint)),
                new Extension("token", new FhirUri(options.TokenEndpoint)),
            ],
        });
        return security;
    }
}
