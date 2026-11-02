using Microsoft.AspNetCore.Http;

namespace MyFhirApi.Api.Search;

/// <summary>One search criterion, e.g. <c>birthdate=ge1980</c> or <c>name:exact=Smith</c>.</summary>
/// <param name="Name">Parameter name without modifier, e.g. <c>name</c>.</param>
/// <param name="Modifier">The part after ':' (e.g. <c>exact</c>, <c>contains</c>), or null.</param>
/// <param name="Value">The raw value. Commas inside it mean OR.</param>
public sealed record SearchCriterion(string Name, string? Modifier, string Value);

/// <summary>
/// A parsed FHIR search query string.
///
/// FHIR concept: search combines criteria with AND and OR:
/// <code>
///   GET /Patient?family=smith&amp;gender=female     -> AND (two different parameters)
///   GET /Patient?birthdate=ge1980&amp;birthdate=lt1990 -> AND (same parameter repeated)
///   GET /Patient?family=smith,jones               -> OR  (comma-separated values)
/// </code>
/// Parameters that start with "_" are "result parameters" that control the response, not the matching.
/// </summary>
public sealed class SearchRequest
{
    public const int DefaultCount = 20;
    public const int MaxCount = 100;

    /// <summary>Result parameters that are handled outside the matching logic.</summary>
    private static readonly HashSet<string> ControlParameters = ["_count", "_offset", "_include", "_pretty", "_format"];

    public List<SearchCriterion> Criteria { get; } = [];
    public List<string> Includes { get; } = [];
    public int Count { get; set; } = DefaultCount;
    public int Offset { get; set; }

    public static SearchRequest Parse(IQueryCollection query)
    {
        var request = new SearchRequest();

        foreach (var (key, values) in query)
        {
            foreach (var value in values.OfType<string>())
            {
                switch (key)
                {
                    case "_count":
                        request.Count = int.TryParse(value, out var c) ? Math.Clamp(c, 0, MaxCount) : DefaultCount;
                        break;
                    case "_offset":
                        request.Offset = int.TryParse(value, out var o) ? Math.Max(0, o) : 0;
                        break;
                    case "_include":
                        request.Includes.Add(value);
                        break;
                    case var k when ControlParameters.Contains(k):
                        break;
                    default:
                        var parts = key.Split(':', 2);
                        request.Criteria.Add(new SearchCriterion(parts[0], parts.Length > 1 ? parts[1] : null, value));
                        break;
                }
            }
        }
        return request;
    }

    /// <summary>Rebuilds a query string (for Bundle self/next/previous links) at the given offset.</summary>
    public string ToQueryString(int offset)
    {
        var pairs = Criteria
            .Select(c => (Key: c.Modifier is null ? c.Name : $"{c.Name}:{c.Modifier}", c.Value))
            .Concat(Includes.Select(i => (Key: "_include", Value: i)))
            .Append((Key: "_count", Value: Count.ToString()))
            .Append((Key: "_offset", Value: offset.ToString()));
        return string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }
}
