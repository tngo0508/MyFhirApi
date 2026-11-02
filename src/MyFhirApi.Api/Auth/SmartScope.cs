namespace MyFhirApi.Api.Auth;

/// <summary>The FHIR REST interactions this server checks permissions for.</summary>
public enum FhirInteraction
{
    Create,  // c
    Read,    // r  (read, vread, history)
    Update,  // u
    Delete,  // d
    Search,  // s
}

/// <summary>
/// SMART on FHIR concept: a scope says <b>whose</b> data, <b>which</b> resource type and <b>what</b> operations.
/// <code>
///   patient/Observation.rs   -> read+search Observations, but only for the patient in the token's "patient" claim
///   user/Patient.cruds       -> everything on Patients the logged-in user is allowed to see
///   system/*.read            -> backend service, read anything (SMART v1 syntax)
/// </code>
/// v1 permissions are <c>read</c>, <c>write</c> or <c>*</c>; v2 permissions are any of the letters <c>c r u d s</c>.
/// See https://hl7.org/fhir/smart-app-launch/scopes-and-launch-context.html
/// </summary>
public sealed record SmartScope(SmartScope.Contexts Context, string ResourceType, string Permissions)
{
    public enum Contexts { Patient, User, System }

    public static bool TryParse(string raw, out SmartScope scope)
    {
        scope = null!;

        // SMART v2 allows extra search filters such as "patient/Observation.rs?category=laboratory".
        // This prototype ignores the filter part (so the scope grants slightly more than asked).
        var withoutQuery = raw.Split('?')[0];

        var slash = withoutQuery.IndexOf('/');
        var dot = withoutQuery.LastIndexOf('.');
        if (slash <= 0 || dot <= slash) return false;

        Contexts? context = withoutQuery[..slash] switch
        {
            "patient" => Contexts.Patient,
            "user" => Contexts.User,
            "system" => Contexts.System,
            _ => null,
        };
        if (context is null) return false;

        var permissions = withoutQuery[(dot + 1)..] switch
        {
            "read" => "rs",       // v1
            "write" => "cud",     // v1
            "*" => "cruds",       // v1
            var v2 when v2.Length > 0 && v2.All(ch => "cruds".Contains(ch)) => v2,
            _ => null,
        };
        if (permissions is null) return false;

        scope = new SmartScope(context.Value, withoutQuery[(slash + 1)..dot], permissions);
        return true;
    }

    public bool Allows(string resourceType, FhirInteraction interaction) =>
        (ResourceType == "*" || ResourceType == resourceType) && Permissions.Contains(Letter(interaction));

    private static char Letter(FhirInteraction interaction) => interaction switch
    {
        FhirInteraction.Create => 'c',
        FhirInteraction.Read => 'r',
        FhirInteraction.Update => 'u',
        FhirInteraction.Delete => 'd',
        FhirInteraction.Search => 's',
        _ => throw new ArgumentOutOfRangeException(nameof(interaction)),
    };
}
