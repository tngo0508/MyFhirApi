using System.Text.RegularExpressions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using MyFhirApi.Api.Auth;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;
using MyFhirApi.Api.Search;

namespace MyFhirApi.Api.Controllers;

/// <summary>
/// The FHIR RESTful API. https://hl7.org/fhir/R4/http.html
///
/// One generic controller serves every resource type in <see cref="SupportedResources"/>,
/// because FHIR uses the same URL patterns for all of them:
/// <code>
///   POST   /{type}                       create
///   GET    /{type}/{id}                  read
///   PUT    /{type}/{id}                  update (or create with a client-chosen id)
///   DELETE /{type}/{id}                  delete
///   GET    /{type}/{id}/_history         history of one resource
///   GET    /{type}/{id}/_history/{vid}   vread (read one specific version)
///   GET    /{type}?param=value           search
/// </code>
/// </summary>
[ApiController]
[Route("")]
public sealed partial class FhirController(
    ResourceRepository repository,
    SearchService search,
    SmartAccessService smart) : ControllerBase
{
    [HttpPost("{type}")]
    public async Task<IActionResult> Create(string type, [FromBody] Resource? resource, CancellationToken ct)
    {
        SupportedResources.Get(type);
        // Always check permissions before looking at the body, so unauthorized callers learn nothing from validation errors.
        var access = smart.Authorize(User, type, FhirInteraction.Create);
        resource = ValidBody(type, resource);
        access.EnsureCanAccess(resource);

        var created = await repository.CreateAsync(resource, ct);
        return ResourceResult(created, StatusCodes.Status201Created);
    }

    [HttpGet("{type}/{id}")]
    public async Task<IActionResult> Read(string type, string id, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Read);

        var resource = await repository.ReadAsync(type, id, ct);
        access.EnsureCanAccess(resource);
        return ResourceResult(resource, StatusCodes.Status200OK);
    }

    [HttpPut("{type}/{id}")]
    public async Task<IActionResult> Update(string type, string id, [FromBody] Resource? resource, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Update);

        resource = ValidBody(type, resource);
        if (!IdPattern().IsMatch(id))
            throw FhirException.BadRequest($"'{id}' is not a valid FHIR id (1-64 characters: letters, digits, '-' and '.').");
        // FHIR rule: the id in the body must match the id in the URL.
        if (resource.Id != id)
            throw FhirException.BadRequest($"Resource id '{resource.Id}' does not match URL id '{id}'.");
        access.EnsureCanAccess(resource);

        var (saved, created) = await repository.UpdateAsync(resource, ParseIfMatch(), ct);
        return ResourceResult(saved, created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    [HttpDelete("{type}/{id}")]
    public async Task<IActionResult> Delete(string type, string id, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Delete);
        if (access.PatientId is not null)
            access.EnsureCanAccess(await repository.ReadAsync(type, id, ct));

        await repository.DeleteAsync(type, id, ct);
        return NoContent();
    }

    [HttpGet("{type}/{id}/_history")]
    public async Task<IActionResult> History(string type, string id, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Read);
        if (access.PatientId is not null)
            access.EnsureCanAccess(await repository.ReadAsync(type, id, ct));

        var versions = await repository.HistoryAsync(type, id, ct);
        return Ok(BundleBuilder.History(BaseUrl, versions));
    }

    [HttpGet("{type}/{id}/_history/{versionId:int}")]
    public async Task<IActionResult> VRead(string type, string id, int versionId, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Read);

        var resource = await repository.VReadAsync(type, id, versionId, ct);
        access.EnsureCanAccess(resource);
        return ResourceResult(resource, StatusCodes.Status200OK);
    }

    [HttpGet("{type}")]
    public async Task<IActionResult> Search(string type, CancellationToken ct)
    {
        SupportedResources.Get(type);
        var access = smart.Authorize(User, type, FhirInteraction.Search);

        var request = SearchRequest.Parse(Request.Query);
        if (access.PatientId is not null)
        {
            // patient/ scope: silently narrow the search to the patient in the token.
            var (name, value) = PatientCompartment.SearchCriterion(type, access.PatientId);
            request.Criteria.Add(new SearchCriterion(name, null, value));
        }

        var result = await search.SearchAsync(type, request, ct);
        return Ok(BundleBuilder.SearchSet(BaseUrl, type, request, result));
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    /// <summary>
    /// FHIR responses for a single resource carry its version in the ETag header
    /// (<c>W/"3"</c>) and its timestamp in Last-Modified. Creates also return a Location header.
    /// </summary>
    private ObjectResult ResourceResult(Resource resource, int status)
    {
        Response.Headers.ETag = $"W/\"{resource.Meta?.VersionId}\"";
        if (resource.Meta?.LastUpdated is { } lastUpdated)
            Response.Headers.LastModified = lastUpdated.ToString("R");
        if (status == StatusCodes.Status201Created)
            Response.Headers.Location = $"{BaseUrl}/{resource.TypeName}/{resource.Id}/_history/{resource.Meta?.VersionId}";
        return new ObjectResult(resource) { StatusCode = status };
    }

    /// <summary>
    /// Returns the parsed body, or throws 400 with the problems that
    /// <see cref="FhirJsonInputFormatter"/> recorded in ModelState.
    /// </summary>
    private Resource ValidBody(string type, Resource? resource)
    {
        if (!ModelState.IsValid || resource is null)
        {
            var messages = ModelState.Values.SelectMany(v => v.Errors)
                .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? e.Exception?.Message : e.ErrorMessage)
                .OfType<string>()
                .DefaultIfEmpty("A FHIR resource body is required.");
            throw FhirException.BadRequest(string.Join("; ", messages));
        }
        if (resource.TypeName != type)
            throw FhirException.BadRequest($"The body is a {resource.TypeName}, but the URL is for {type}.");
        return resource;
    }

    /// <summary>Reads the version number from an If-Match header such as <c>W/"2"</c>.</summary>
    private int? ParseIfMatch()
    {
        var header = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(header)) return null;
        var match = ETagPattern().Match(header);
        return match.Success
            ? int.Parse(match.Groups[1].Value)
            : throw FhirException.BadRequest($"If-Match '{header}' must look like W/\"<version>\".");
    }

    [GeneratedRegex("""^(?:W/)?"(\d+)"$""")]
    private static partial Regex ETagPattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-\.]{1,64}$")]
    private static partial Regex IdPattern();
}
