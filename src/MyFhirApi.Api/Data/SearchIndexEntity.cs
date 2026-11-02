namespace MyFhirApi.Api.Data;

/// <summary>
/// One searchable value extracted from the <b>current</b> version of a resource.
/// A Patient named "John Smith" gets rows such as (name, "john"), (name, "smith"), (family, "smith").
/// Each search parameter type uses different columns:
/// <list type="bullet">
///   <item>String: <see cref="StringValue"/> (normalized) and <see cref="StringExact"/></item>
///   <item>Token: <see cref="TokenSystem"/> + <see cref="TokenCode"/></item>
///   <item>Date: <see cref="DateStart"/> and <see cref="DateEnd"/>, stored as a range (see SearchIndexer)</item>
///   <item>Reference: <see cref="ReferenceValue"/>, e.g. "Patient/123"</item>
/// </list>
/// </summary>
public class SearchIndexEntity
{
    public long Id { get; set; }

    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public required string ParamName { get; set; }

    public string? StringValue { get; set; }
    public string? StringExact { get; set; }

    public string? TokenSystem { get; set; }
    public string? TokenCode { get; set; }

    public DateTimeOffset? DateStart { get; set; }
    public DateTimeOffset? DateEnd { get; set; }

    public string? ReferenceValue { get; set; }
}
