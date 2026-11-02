using Hl7.Fhir.Model;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;

namespace MyFhirApi.Api.Search;

/// <summary>
/// FHIR concept: a <b>Bundle</b> is a container of resources. Its <c>type</c> says what it is for:
/// <c>searchset</c> (search results), <c>history</c> (versions), <c>transaction</c> (several writes in one go), and others.
/// </summary>
public static class BundleBuilder
{
    public static Bundle SearchSet(string baseUrl, string type, SearchRequest request, SearchResult result)
    {
        var bundle = new Bundle
        {
            Id = Guid.NewGuid().ToString(),
            Type = Bundle.BundleType.Searchset,
            Total = result.Total,
            Meta = new Meta { LastUpdated = DateTimeOffset.UtcNow },
        };

        // Paging links. The client follows "next" instead of building URLs itself.
        string Link(int offset) => $"{baseUrl}/{type}?{request.ToQueryString(offset)}";
        bundle.Link.Add(new Bundle.LinkComponent { Relation = "self", Url = Link(request.Offset) });
        bundle.Link.Add(new Bundle.LinkComponent { Relation = "first", Url = Link(0) });
        if (request.Offset > 0)
            bundle.Link.Add(new Bundle.LinkComponent { Relation = "previous", Url = Link(Math.Max(0, request.Offset - request.Count)) });
        if (request.Count > 0 && request.Offset + request.Count < result.Total)
            bundle.Link.Add(new Bundle.LinkComponent { Relation = "next", Url = Link(request.Offset + request.Count) });

        // search.mode tells the client whether an entry matched, or was pulled in by _include.
        foreach (var resource in result.Matches)
            bundle.Entry.Add(Entry(baseUrl, resource, Bundle.SearchEntryMode.Match));
        foreach (var resource in result.Included)
            bundle.Entry.Add(Entry(baseUrl, resource, Bundle.SearchEntryMode.Include));

        if (result.Warnings.Count > 0)
        {
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                Resource = OperationOutcomeFactory.Create(
                    OperationOutcome.IssueSeverity.Warning, OperationOutcome.IssueType.NotSupported, result.Warnings.ToArray()),
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Outcome },
            });
        }
        return bundle;
    }

    public static Bundle History(string baseUrl, IEnumerable<ResourceEntity> versions)
    {
        var bundle = new Bundle { Id = Guid.NewGuid().ToString(), Type = Bundle.BundleType.History };
        foreach (var v in versions)
        {
            var url = $"{v.ResourceType}/{v.ResourceId}";
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl}/{url}",
                Resource = v.IsDeleted ? null : FhirJson.Parse(v.Json!),
                // Each history entry records which HTTP call produced that version.
                Request = new Bundle.RequestComponent
                {
                    Method = v.IsDeleted ? Bundle.HTTPVerb.DELETE : v.VersionId == 1 ? Bundle.HTTPVerb.POST : Bundle.HTTPVerb.PUT,
                    Url = v.VersionId == 1 && !v.IsDeleted ? v.ResourceType : url,
                },
                Response = new Bundle.ResponseComponent
                {
                    Status = v.IsDeleted ? "204" : v.VersionId == 1 ? "201" : "200",
                    Etag = $"W/\"{v.VersionId}\"",
                    LastModified = v.LastUpdated,
                },
            });
        }
        bundle.Total = bundle.Entry.Count;
        return bundle;
    }

    private static Bundle.EntryComponent Entry(string baseUrl, Resource resource, Bundle.SearchEntryMode mode) => new()
    {
        FullUrl = $"{baseUrl}/{resource.TypeName}/{resource.Id}",
        Resource = resource,
        Search = new Bundle.SearchComponent { Mode = mode },
    };
}
