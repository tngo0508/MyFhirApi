using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using MyFhirApi.Api.Data;
using MyFhirApi.Api.Fhir;

namespace MyFhirApi.Api.Search;

/// <summary>
/// Extracts searchable values from a resource and turns them into <see cref="SearchIndexEntity"/> rows.
///
/// For each search parameter in <see cref="SupportedResources"/> we:
///  1. evaluate its FHIRPath expression with the Firely SDK (e.g. "Patient.name.family"), and
///  2. convert each matching element into index values according to the parameter's type.
/// </summary>
public static class SearchIndexer
{
    public static List<SearchIndexEntity> BuildIndex(Resource resource)
    {
        var definition = SupportedResources.Get(resource.TypeName);
        var rows = new List<SearchIndexEntity>();

        foreach (var param in definition.SearchParameters)
        {
            foreach (var element in resource.Select(param.Expression).OfType<Base>())
            {
                rows.AddRange(param.Type switch
                {
                    SearchParamKind.String => StringValues(element).Select(s => Row(resource, param, r =>
                    {
                        r.StringValue = Normalize(s);
                        r.StringExact = Truncate(s);
                    })),
                    SearchParamKind.Token => TokenValues(element).Select(t => Row(resource, param, r =>
                    {
                        r.TokenSystem = Truncate(t.System);
                        r.TokenCode = Truncate(t.Code);
                    })),
                    SearchParamKind.Date => DateValues(element).Select(d => Row(resource, param, r =>
                    {
                        r.DateStart = d.Start;
                        r.DateEnd = d.End;
                    })),
                    SearchParamKind.Reference => ReferenceValues(element, param.TargetType).Select(v => Row(resource, param, r =>
                    {
                        r.ReferenceValue = Truncate(v);
                    })),
                    _ => [],
                });
            }
        }
        return rows;
    }

    /// <summary>String search is case-insensitive, so we store a lower-cased copy.</summary>
    public static string Normalize(string s) => Truncate(s.Trim().ToLowerInvariant())!;

    private static string? Truncate(string? s) => s is { Length: > 256 } ? s[..256] : s;

    private static SearchIndexEntity Row(Resource resource, SearchParameterDefinition param, Action<SearchIndexEntity> fill)
    {
        var row = new SearchIndexEntity
        {
            ResourceType = resource.TypeName,
            ResourceId = resource.Id ?? throw new InvalidOperationException("Resource must have an id before indexing."),
            ParamName = param.Name,
        };
        fill(row);
        return row;
    }

    private static IEnumerable<string> StringValues(Base element) => element switch
    {
        // A HumanName matches on any of its parts.
        HumanName name => new[] { name.Family, name.Text }
            .Concat(name.Given ?? [])
            .Concat(name.Prefix ?? [])
            .Concat(name.Suffix ?? [])
            .OfType<string>()
            .Where(s => !string.IsNullOrWhiteSpace(s)),
        PrimitiveType p when PrimitiveText(p) is { } text => [text],
        _ => [],
    };

    private static IEnumerable<(string? System, string Code)> TokenValues(Base element) => element switch
    {
        Identifier { Value: { } value } id => [(id.System, value)],
        Coding { Code: { } code } c => [(c.System, code)],
        CodeableConcept cc => (cc.Coding ?? [])
            .Where(c => c.Code is not null)
            .Select(c => (c.System, c.Code!)),
        // Plain codes such as Patient.gender or Observation.status have no system in the instance.
        PrimitiveType p when PrimitiveText(p) is { } code => [((string?)null, code)],
        _ => [],
    };

    /// <summary>The value of a FHIR primitive (string, code, date...) as it appears in JSON.</summary>
    private static string? PrimitiveText(PrimitiveType p) => p.JsonValue?.ToString();

    private static IEnumerable<FhirDateRange> DateValues(Base element)
    {
        switch (element)
        {
            case Period period:
                var start = FhirDateRange.TryParse(period.Start, out var s) ? s.Start : FhirDateRange.MinValue;
                var end = FhirDateRange.TryParse(period.End, out var e) ? e.End : FhirDateRange.MaxValue;
                yield return new FhirDateRange(start, end);
                break;
            case PrimitiveType p when FhirDateRange.TryParse(PrimitiveText(p), out var range):
                yield return range;
                break;
        }
    }

    private static IEnumerable<string> ReferenceValues(Base element, string? targetType)
    {
        if (element is not ResourceReference { Reference: { } reference }) yield break;

        // Normalize absolute URLs ("http://server/fhir/Patient/1") and versioned references
        // ("Patient/1/_history/2") to the plain relative form "Patient/1".
        var historyIndex = reference.IndexOf("/_history/", StringComparison.Ordinal);
        if (historyIndex >= 0) reference = reference[..historyIndex];
        var parts = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) yield break;
        var relative = $"{parts[^2]}/{parts[^1]}";

        if (targetType is null || parts[^2] == targetType)
            yield return relative;
    }
}
