using Hl7.Fhir.Model;
using Microsoft.EntityFrameworkCore;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;

namespace MyFhirApi.Api.Search;

public sealed record SearchResult(
    IReadOnlyList<Resource> Matches,
    IReadOnlyList<Resource> Included,
    int Total,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a <see cref="SearchRequest"/> into SQL using the <see cref="SearchIndexEntity"/> table.
///
/// Each criterion becomes "ResourceId IN (SELECT ResourceId FROM SearchIndex WHERE ...)".
/// Several criteria are combined with AND, and comma-separated values inside a criterion become a UNION (OR).
/// </summary>
public sealed class SearchService(FhirDbContext db)
{
    public async Task<SearchResult> SearchAsync(string type, SearchRequest request, CancellationToken ct = default)
    {
        var definition = SupportedResources.Get(type);
        var warnings = new List<string>();

        var query = db.Resources.AsNoTracking()
            .Where(r => r.ResourceType == type && r.IsCurrent && !r.IsDeleted);

        foreach (var criterion in request.Criteria)
        {
            if (criterion.Name == "_id")
            {
                var ids = criterion.Value.Split(',');
                query = query.Where(r => ids.Contains(r.ResourceId));
                continue;
            }

            var param = definition.FindParameter(criterion.Name);
            if (param is null)
            {
                // FHIR default ("lenient") behavior: ignore unknown parameters and tell the client.
                warnings.Add($"Search parameter '{criterion.Name}' is not supported for {type} and was ignored.");
                continue;
            }

            IQueryable<string>? matchingIds = null;
            foreach (var value in criterion.Value.Split(','))
            {
                var ids = MatchingIds(type, param, criterion.Modifier, value);
                matchingIds = matchingIds is null ? ids : matchingIds.Union(ids);
            }
            query = query.Where(r => matchingIds!.Contains(r.ResourceId));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderBy(r => r.Pk) // a stable order so paging doesn't skip or repeat resources
            .Skip(request.Offset)
            .Take(request.Count)
            .ToListAsync(ct);
        var matches = page.Select(r => FhirJson.Parse(r.Json!)).ToList();

        var included = await IncludesAsync(type, definition, request.Includes, page, warnings, ct);
        return new SearchResult(matches, included, total, warnings);
    }

    /// <summary>The ids of resources whose index has a value matching one search value.</summary>
    private IQueryable<string> MatchingIds(string type, SearchParameterDefinition param, string? modifier, string value)
    {
        var index = db.SearchIndex.Where(i => i.ResourceType == type && i.ParamName == param.Name);

        index = param.Type switch
        {
            SearchParamKind.String => MatchString(index, modifier, value),
            SearchParamKind.Token => MatchToken(index, value),
            SearchParamKind.Date => MatchDate(index, value),
            SearchParamKind.Reference => MatchReference(index, param, value),
            _ => throw FhirException.BadRequest($"Unsupported parameter type {param.Type}."),
        };
        if (modifier is not null && param.Type != SearchParamKind.String)
            throw FhirException.BadRequest($"Modifier ':{modifier}' is not supported for '{param.Name}'.");

        return index.Select(i => i.ResourceId);
    }

    // String: case-insensitive "starts with" by default; ":exact" and ":contains" modifiers.
    private static IQueryable<SearchIndexEntity> MatchString(IQueryable<SearchIndexEntity> index, string? modifier, string value)
    {
        var normalized = SearchIndexer.Normalize(value);
        return modifier switch
        {
            null => index.Where(i => i.StringValue!.StartsWith(normalized)),
            "contains" => index.Where(i => i.StringValue!.Contains(normalized)),
            "exact" => index.Where(i => i.StringExact == value),
            _ => throw FhirException.BadRequest($"String modifier ':{modifier}' is not supported."),
        };
    }

    // Token: "code", "system|code", "|code" (code with no system), or "system|" (any code in the system).
    private static IQueryable<SearchIndexEntity> MatchToken(IQueryable<SearchIndexEntity> index, string value)
    {
        var bar = value.IndexOf('|');
        if (bar < 0) return index.Where(i => i.TokenCode == value);

        var system = value[..bar];
        var code = value[(bar + 1)..];
        if (system.Length == 0) return index.Where(i => i.TokenSystem == null && i.TokenCode == code);
        if (code.Length == 0) return index.Where(i => i.TokenSystem == system);
        return index.Where(i => i.TokenSystem == system && i.TokenCode == code);
    }

    // Date: an optional two-letter prefix, then a date of any precision. Both sides are ranges.
    private static IQueryable<SearchIndexEntity> MatchDate(IQueryable<SearchIndexEntity> index, string value)
    {
        var prefix = "eq";
        if (value.Length > 2 && char.IsLetter(value[0]))
        {
            prefix = value[..2];
            value = value[2..];
        }
        if (!FhirDateRange.TryParse(value, out var p))
            throw FhirException.BadRequest($"'{value}' is not a valid FHIR date.");

        var (start, end) = (p.Start, p.End);
        return prefix switch
        {
            "eq" => index.Where(i => i.DateStart >= start && i.DateEnd <= end),     // fully inside the search range
            "ne" => index.Where(i => i.DateStart < start || i.DateEnd > end),
            "gt" => index.Where(i => i.DateEnd > end),                              // some part after the range
            "lt" => index.Where(i => i.DateStart < start),                          // some part before the range
            "ge" => index.Where(i => i.DateEnd > start),
            "le" => index.Where(i => i.DateStart < end),
            _ => throw FhirException.BadRequest($"Date prefix '{prefix}' is not supported."),
        };
    }

    // Reference: "Patient/123", or just "123" when the parameter has a single target type.
    private static IQueryable<SearchIndexEntity> MatchReference(
        IQueryable<SearchIndexEntity> index, SearchParameterDefinition param, string value)
    {
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var reference = parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}"
            : param.TargetType is not null ? $"{param.TargetType}/{value}"
            : throw FhirException.BadRequest($"Reference '{value}' must look like 'Type/id'.");
        return index.Where(i => i.ReferenceValue == reference);
    }

    /// <summary>
    /// FHIR concept: <c>_include</c> adds referenced resources to the same Bundle, which saves the client extra calls.
    /// <c>GET /Observation?_include=Observation:subject</c> returns the Observations plus their Patients.
    /// </summary>
    private async Task<List<Resource>> IncludesAsync(
        string type, ResourceDefinition definition, List<string> includes,
        List<ResourceEntity> page, List<string> warnings, CancellationToken ct)
    {
        var result = new List<Resource>();
        if (includes.Count == 0 || page.Count == 0) return result;

        var pageIds = page.Select(r => r.ResourceId).ToList();
        foreach (var include in includes)
        {
            var parts = include.Split(':');
            if (parts.Length != 2 || parts[0] != type || !definition.Includes.Contains(parts[1]))
            {
                warnings.Add($"_include={include} is not supported and was ignored.");
                continue;
            }

            var references = await db.SearchIndex
                .Where(i => i.ResourceType == type && i.ParamName == parts[1] && pageIds.Contains(i.ResourceId))
                .Select(i => i.ReferenceValue!)
                .Distinct()
                .ToListAsync(ct);

            foreach (var group in references.Select(r => r.Split('/')).GroupBy(r => r[0], r => r[1]))
            {
                var ids = group.ToList();
                var rows = await db.Resources.AsNoTracking()
                    .Where(r => r.ResourceType == group.Key && ids.Contains(r.ResourceId) && r.IsCurrent && !r.IsDeleted)
                    .ToListAsync(ct);
                result.AddRange(rows.Select(r => FhirJson.Parse(r.Json!)));
            }
        }
        // Several includes can point to the same resource; return each one once.
        return result.DistinctBy(r => $"{r.TypeName}/{r.Id}").ToList();
    }
}
