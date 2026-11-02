namespace MyFhirApi.Api.Data;

/// <summary>
/// One row per <b>version</b> of a resource.
///
/// FHIR concept: resources are versioned. Every create/update/delete adds a new version
/// (<c>meta.versionId</c>), and old versions stay readable through <c>_history</c>.
/// We store the whole resource as JSON. The database doesn't need to understand FHIR's
/// deeply nested structure; for searching we keep separate index rows (<see cref="SearchIndexEntity"/>).
/// This "JSON blob + search index" design is how most real FHIR servers work.
/// </summary>
public class ResourceEntity
{
    /// <summary>Surrogate key; also gives a stable insertion order for paging.</summary>
    public long Pk { get; set; }

    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public int VersionId { get; set; }
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>True for the newest version of the resource. Search and read only look at current rows.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>A delete is stored as a "tombstone" version with no JSON.</summary>
    public bool IsDeleted { get; set; }

    public string? Json { get; set; }
}
