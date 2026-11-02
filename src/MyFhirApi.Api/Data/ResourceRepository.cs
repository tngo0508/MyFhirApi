using Hl7.Fhir.Model;
using Hl7.Fhir.Validation;
using Microsoft.EntityFrameworkCore;
using MyFhirApi.Api.Fhir;
using MyFhirApi.Api.Search;

namespace MyFhirApi.Api.Data;

/// <summary>
/// Stores and loads versioned FHIR resources. This is where FHIR's versioning rules live:
/// <list type="bullet">
///   <item>create: the server assigns <c>id</c>, <c>meta.versionId = 1</c> and <c>meta.lastUpdated</c></item>
///   <item>update: adds version N+1 and keeps the older versions for <c>_history</c></item>
///   <item>delete: adds a "tombstone" version; a later read returns 410 Gone</item>
/// </list>
/// Every write also rebuilds the resource's search index rows in the same database transaction.
/// </summary>
public sealed class ResourceRepository(FhirDbContext db, TimeProvider clock)
{
    public async Task<Resource> CreateAsync(Resource resource, CancellationToken ct = default)
    {
        // FHIR rule: on create the server assigns the id and ignores any id the client sent.
        resource.Id = Guid.NewGuid().ToString();
        await WriteVersionAsync(resource, versionId: 1, previous: null, ct);
        return resource;
    }

    /// <summary>
    /// FHIR "update as create": a PUT to an id that doesn't exist yet creates it with that id.
    /// <paramref name="ifMatchVersion"/> implements optimistic locking (the If-Match header): the update only
    /// succeeds if the client saw the latest version, so concurrent edits aren't silently lost.
    /// </summary>
    public async Task<(Resource Resource, bool Created)> UpdateAsync(
        Resource resource, int? ifMatchVersion, CancellationToken ct = default)
    {
        var id = resource.Id ?? throw FhirException.BadRequest("Resource id is required for update.");
        var current = await CurrentAsync(resource.TypeName, id, ct);

        if (ifMatchVersion is not null && current?.VersionId != ifMatchVersion)
            throw FhirException.PreconditionFailed(
                $"Version conflict: If-Match was {ifMatchVersion}, current version is {current?.VersionId.ToString() ?? "none"}.");

        var version = (current?.VersionId ?? 0) + 1;
        await WriteVersionAsync(resource, version, current, ct);
        return (resource, current is null || current.IsDeleted);
    }

    public async Task DeleteAsync(string type, string id, CancellationToken ct = default)
    {
        var current = await CurrentAsync(type, id, ct);
        if (current is null || current.IsDeleted) return; // delete is idempotent

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        current.IsCurrent = false;
        db.Resources.Add(new ResourceEntity
        {
            ResourceType = type,
            ResourceId = id,
            VersionId = current.VersionId + 1,
            LastUpdated = clock.GetUtcNow(),
            IsCurrent = true,
            IsDeleted = true,
        });
        await db.SearchIndex.Where(i => i.ResourceType == type && i.ResourceId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Returns the current version; throws 404 if it never existed and 410 if it was deleted.</summary>
    public async Task<Resource> ReadAsync(string type, string id, CancellationToken ct = default)
    {
        var current = await CurrentAsync(type, id, ct)
                      ?? throw FhirException.NotFound($"{type}/{id} was not found.");
        if (current.IsDeleted)
            throw FhirException.Gone($"{type}/{id} was deleted.");
        return FhirJson.Parse(current.Json!);
    }

    public async Task<Resource> VReadAsync(string type, string id, int versionId, CancellationToken ct = default)
    {
        var row = await db.Resources.AsNoTracking()
                      .SingleOrDefaultAsync(r => r.ResourceType == type && r.ResourceId == id && r.VersionId == versionId, ct)
                  ?? throw FhirException.NotFound($"{type}/{id}/_history/{versionId} was not found.");
        if (row.IsDeleted)
            throw FhirException.Gone($"{type}/{id} version {versionId} is a deletion.");
        return FhirJson.Parse(row.Json!);
    }

    /// <summary>All versions, newest first, including deletions.</summary>
    public async Task<List<ResourceEntity>> HistoryAsync(string type, string id, CancellationToken ct = default)
    {
        var rows = await db.Resources.AsNoTracking()
            .Where(r => r.ResourceType == type && r.ResourceId == id)
            .OrderByDescending(r => r.VersionId)
            .ToListAsync(ct);
        return rows.Count > 0 ? rows : throw FhirException.NotFound($"{type}/{id} was not found.");
    }

    public Task<bool> ExistsAsync(string type, string id, CancellationToken ct = default) =>
        db.Resources.AnyAsync(r => r.ResourceType == type && r.ResourceId == id && r.IsCurrent && !r.IsDeleted, ct);

    private Task<ResourceEntity?> CurrentAsync(string type, string id, CancellationToken ct) =>
        db.Resources.SingleOrDefaultAsync(r => r.ResourceType == type && r.ResourceId == id && r.IsCurrent, ct);

    private async Task WriteVersionAsync(Resource resource, int versionId, ResourceEntity? previous, CancellationToken ct)
    {
        // meta.versionId and meta.lastUpdated are owned by the server, never by the client.
        var now = clock.GetUtcNow();
        resource.Meta ??= new Meta();
        resource.Meta.VersionId = versionId.ToString();
        resource.Meta.LastUpdated = now;

        await ValidateAsync(resource, ct);
        var indexRows = SearchIndexer.BuildIndex(resource);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (previous is not null) previous.IsCurrent = false;

        db.Resources.Add(new ResourceEntity
        {
            ResourceType = resource.TypeName,
            ResourceId = resource.Id!,
            VersionId = versionId,
            LastUpdated = now,
            IsCurrent = true,
            Json = FhirJson.Serialize(resource),
        });

        // Only the current version is searchable, so replace the old index rows.
        await db.SearchIndex
            .Where(i => i.ResourceType == resource.TypeName && i.ResourceId == resource.Id)
            .ExecuteDeleteAsync(ct);
        db.SearchIndex.AddRange(indexRows);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Two levels of validation:
    ///  1. Structural rules from the FHIR spec built into the Firely model classes
    ///     (required elements, cardinality, value formats).
    ///  2. Referential integrity: an Observation's subject must point to a Patient that exists.
    ///     (FHIR servers may choose how strict to be here; many reject dangling references like this.)
    /// </summary>
    private async Task ValidateAsync(Resource resource, CancellationToken ct)
    {
        var issues = resource.Validate();
        if (issues.Count > 0)
            throw FhirException.BadRequest(string.Join("; ", issues.Select(i => i.Message)));

        if (resource is Observation { Subject.Reference: { } subject } &&
            subject.Split('/') is [var refType, var refId] &&
            !await ExistsAsync(refType, refId, ct))
        {
            throw FhirException.BadRequest($"Observation.subject references '{subject}', which does not exist.");
        }
    }
}
