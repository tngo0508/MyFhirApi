using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Microsoft.EntityFrameworkCore;
using MyFhirApi.Api.Fhir;

namespace MyFhirApi.Api.Data;

/// <summary>
/// Loads synthetic sample data from <c>SampleData/*.json</c> when the database is empty.
///
/// The files are FHIR <b>transaction Bundles</b> in the same shape that <b>Synthea</b> produces.
/// Synthea is the standard open-source generator of realistic but fake patients (https://synthea.mitre.org).
/// Never use real patient data (PHI) for development.
///
/// In Synthea bundles, resources refer to each other with temporary ids such as
/// <c>"reference": "urn:uuid:1234..."</c>. A FHIR server processing a transaction must replace those
/// with real references like <c>Patient/1234...</c>. We do the same thing here.
/// Resource types this server doesn't support (Encounter, Condition, Claim...) are skipped.
/// </summary>
public sealed class SampleDataLoader(FhirDbContext db, ResourceRepository repository, ILogger<SampleDataLoader> logger)
{
    public async Task LoadIfEmptyAsync(string folder, CancellationToken ct = default)
    {
        if (await db.Resources.AnyAsync(ct))
        {
            logger.LogInformation("Database already has data; skipping sample data.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Sample data folder {Folder} not found.", folder);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order())
        {
            var bundle = FhirJson.Parse<Bundle>(await File.ReadAllTextAsync(file, ct));
            var (loaded, skipped) = await LoadBundleAsync(bundle, ct);
            logger.LogInformation("Loaded {Loaded} resources from {File} ({Skipped} of unsupported types skipped).",
                loaded, Path.GetFileName(file), skipped);
        }
    }

    public async Task<(int Loaded, int Skipped)> LoadBundleAsync(Bundle bundle, CancellationToken ct = default)
    {
        // 1. Give every entry a real id and remember "urn:uuid:..." -> "Type/id".
        var urnMap = new Dictionary<string, string>();
        foreach (var entry in bundle.Entry)
        {
            if (entry.Resource is not { } resource) continue;
            if (entry.FullUrl?.StartsWith("urn:uuid:") == true)
                resource.Id = entry.FullUrl["urn:uuid:".Length..];
            resource.Id ??= Guid.NewGuid().ToString();
            if (entry.FullUrl is not null)
                urnMap[entry.FullUrl] = $"{resource.TypeName}/{resource.Id}";
        }

        // 2. Store supported resources. Patients go first so Observation.subject references resolve.
        int loaded = 0, skipped = 0;
        var resources = bundle.Entry.Select(e => e.Resource).OfType<Resource>()
            .OrderBy(r => r is Patient ? 0 : 1);
        foreach (var resource in resources)
        {
            if (!SupportedResources.All.ContainsKey(resource.TypeName))
            {
                skipped++;
                continue;
            }

            // descendants() is a FHIRPath function that walks the whole resource tree.
            foreach (var reference in resource.Select("descendants()").OfType<ResourceReference>())
            {
                if (reference.Reference is { } r && urnMap.TryGetValue(r, out var resolved))
                    reference.Reference = resolved;
            }

            await repository.UpdateAsync(resource, ifMatchVersion: null, ct);
            loaded++;
        }
        return (loaded, skipped);
    }
}
