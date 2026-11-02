namespace MyFhirApi.Api.Search;

/// <summary>
/// FHIR concept: every search parameter has a <b>type</b>, and the type decides how matching works.
/// See https://hl7.org/fhir/R4/search.html#ptypes
/// <list type="bullet">
///   <item><c>String</c>: case-insensitive "starts with" (e.g. name=smi matches "Smith")</item>
///   <item><c>Token</c>: exact code, optionally with a system: <c>system|code</c></item>
///   <item><c>Date</c>: ranges with prefixes such as <c>ge2020-01-01</c></item>
///   <item><c>Reference</c>: a link to another resource, e.g. <c>Patient/123</c></item>
/// </list>
/// R4 has more types (number, quantity, uri, composite, special) that this prototype leaves out.
/// </summary>
public enum SearchParamKind
{
    String,
    Token,
    Date,
    Reference,
}

/// <summary>
/// One search parameter that this server supports.
/// </summary>
/// <param name="Name">The query-string name, e.g. <c>family</c>.</param>
/// <param name="Type">How values are matched.</param>
/// <param name="Expression">
/// A FHIRPath expression that selects the indexed values from a resource. These expressions are copied
/// from the official R4 SearchParameter definitions, e.g. <c>Patient.name.family</c>.
/// </param>
/// <param name="TargetType">For reference parameters: only index references to this resource type.</param>
public sealed record SearchParameterDefinition(
    string Name,
    SearchParamKind Type,
    string Expression,
    string? TargetType = null);
