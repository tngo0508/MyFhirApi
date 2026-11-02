# 1. Architecture

## System context

Who talks to the API, and what the API depends on.

```mermaid
flowchart LR
    subgraph clients["Clients"]
        app["Health app / EHR / portal"]
        dev["Developer<br/>(.http file, curl, browser)"]
        tests["Integration tests<br/>(WebApplicationFactory)"]
    end

    auth["Authorization server<br/>(Entra ID, Keycloak, Okta...)<br/>or dotnet user-jwts locally"]

    subgraph api["MyFhirApi (ASP.NET Core, .NET 10)"]
        rest["FHIR REST API<br/>application/fhir+json"]
    end

    db[("SQL Server<br/>LocalDB by default")]
    firely["Firely .NET SDK<br/>(NuGet: Hl7.Fhir.R4)"]

    app -- "1. log in, get token" --> auth
    app -- "2. HTTP + Bearer token" --> rest
    dev --> rest
    tests --> rest
    rest -- "EF Core" --> db
    rest -. "uses: models, JSON, FHIRPath, validation" .-> firely
    rest -. "trusts tokens signed by" .-> auth
```

The API never issues tokens. It only **validates** them. In production an authorization server issues them;
locally, `dotnet user-jwts` plays that role.

## Layers

Each folder is one layer. Arrows show "calls / depends on". The dotted box, `Fhir/`, is shared plumbing that
every layer uses. (Class-level dependencies are in [Class diagrams](03-class-diagrams.md).)

```mermaid
flowchart TB
    http(["HTTP"]) --> web

    subgraph web["Controllers/ — web layer"]
        direction LR
        fc["FhirController"] ~~~ mc["MetadataController"] ~~~ sc["SmartConfigController"]
    end

    subgraph auth["Auth/ — security"]
        direction LR
        sas["SmartAccessService"] ~~~ scope["SmartScope"] ~~~ pc["PatientCompartment"]
    end

    subgraph search["Search/ — querying"]
        direction LR
        ss["SearchService"] ~~~ si["SearchIndexer"] ~~~ bb["BundleBuilder"]
    end

    subgraph data["Data/ — storage"]
        direction LR
        repo["ResourceRepository"] ~~~ ctx["FhirDbContext"] ~~~ sdl["SampleDataLoader"]
    end

    sql[("SQL Server")]

    subgraph fhir["Fhir/ — shared FHIR plumbing"]
        direction LR
        sr["SupportedResources"] ~~~ fmt["JSON formatters"] ~~~ exf["FhirException + filter"]
    end

    web -- "1 authorize" --> auth
    web -- "2a read / write" --> data
    web -- "2b search" --> search
    search -- "queries index" --> data
    data -- "indexes on write" --> search
    data -- "EF Core" --> sql
    web & search & data -.-> fhir

    style fhir stroke-dasharray: 5 5
```

| Folder | Responsibility | Knows about HTTP? | Knows about SQL? |
|---|---|---|---|
| `Controllers/` | Map FHIR URLs to operations; set FHIR headers (ETag, Location, Last-Modified) | Yes | No |
| `Fhir/` | FHIR JSON in and out, errors as OperationOutcome, the list of supported types | Yes (formatters, filter) | No |
| `Auth/` | Decide whether a token may do something | Only via `ClaimsPrincipal` | No |
| `Search/` | Index values and turn search parameters into queries | No (`SearchRequest` reads the query collection) | Through EF LINQ |
| `Data/` | Versioned storage and transactions | No | Yes (EF Core) |

**Design choice: one generic controller.** FHIR uses the same URL patterns for every resource type, so
`FhirController` takes `{type}` as a route value and looks it up in `SupportedResources`. Adding a resource type
doesn't add a controller.

## ASP.NET Core request pipeline

What happens to every HTTP request, in order.

```mermaid
flowchart TD
    req(["HTTP request"]) --> authn["UseAuthentication()<br/>JwtBearer reads the Bearer token<br/>and fills HttpContext.User (or leaves it anonymous)"]
    authn --> routing["MapControllers() routing<br/>literal routes win: /metadata, /.well-known/... <br/>before {type} patterns"]
    routing --> bind["Model binding<br/>[FromBody] Resource? → FhirJsonInputFormatter<br/>parse errors go into ModelState (no exception)"]
    bind --> action["Controller action"]
    action --> a1["1. SupportedResources.Get(type) → 404 if unknown"]
    a1 --> a2["2. SmartAccessService.Authorize → 401 / 403"]
    a2 --> a3["3. ValidBody: ModelState + type check → 400"]
    a3 --> a4["4. Repository / SearchService"]
    a4 --> result["ObjectResult(Resource or Bundle)"]
    result --> out["FhirJsonOutputFormatter<br/>application/fhir+json, honors _pretty"]
    out --> resp(["HTTP response"])

    a1 & a2 & a3 & a4 -. "throw FhirException" .-> filter["FhirExceptionFilter<br/>→ OperationOutcome + status code<br/>(+ WWW-Authenticate on 401)"]
    filter --> out
```

Two things to notice:

- **Authorization runs before body validation** (step 2 before 3). A caller without permission gets 401/403 and
  learns nothing from validation messages. The formatter records parse errors instead of throwing, so this order holds.
- **Every error is an OperationOutcome.** Code anywhere throws `FhirException`, and one filter converts it.
  Unexpected exceptions become a 500 OperationOutcome and are logged.

## Startup

```mermaid
flowchart TD
    start(["dotnet run"]) --> cfg["Load config<br/>appsettings.json → appsettings.{Environment}.json<br/>→ user secrets (Development) → env vars → command line"]
    cfg --> di["Register services (see table below)"]
    di --> build["builder.Build()"]
    build --> ensure["Database.EnsureCreatedAsync()<br/>creates DB + tables if missing"]
    ensure --> sample{"SampleData:LoadOnStartup?<br/>(true in Development)"}
    sample -- yes --> empty{"Any rows in Resources?"}
    empty -- no --> load["SampleDataLoader loads SampleData/*.json"]
    empty -- yes --> skip["skip"]
    sample -- no --> run
    load --> run["UseAuthentication, MapControllers, Run<br/>listening on http://localhost:5025"]
    skip --> run
```

## Dependency injection

Registered in [`Program.cs`](../src/MyFhirApi.Api/Program.cs).

| Service | Lifetime | Why that lifetime |
|---|---|---|
| `FhirDbContext` | Scoped (per request) | EF Core contexts aren't thread-safe; one per request |
| `ResourceRepository` | Scoped | Uses the scoped `FhirDbContext` |
| `SearchService` | Scoped | Uses the scoped `FhirDbContext` |
| `SampleDataLoader` | Scoped | Uses the repository; only resolved at startup |
| `SmartAccessService` | Singleton | Stateless; reads `IOptions<SmartOptions>` |
| `TimeProvider` | Singleton (`TimeProvider.System`) | Tests could swap in a fake clock |
| `SmartOptions` | Options (`Smart` config section) | `Enabled`, `AuthorizeEndpoint`, `TokenEndpoint` |
| JwtBearer | Authentication handler | Reads `Authentication:Schemes:Bearer`; `MapInboundClaims = false` keeps `scope` and `patient` claim names |

Static helpers (`SupportedResources`, `SearchIndexer`, `BundleBuilder`, `FhirJson`, `PatientCompartment`) don't go
through DI: they're pure functions with no state.

## Configuration

| Key | Default (`appsettings.json`) | Development | Used by |
|---|---|---|---|
| `ConnectionStrings:Fhir` | LocalDB, database `MyFhirApi` | same | `FhirDbContext` |
| `Smart:Enabled` | `true` | `false` | `SmartAccessService` |
| `Smart:AuthorizeEndpoint` / `TokenEndpoint` | `https://example.org/...` | same | `/metadata`, SMART discovery |
| `SampleData:LoadOnStartup` | `false` | `true` | `Program.cs` |
| `Authentication:Schemes:Bearer:*` | not set | `dotnet-user-jwts` issuer and audiences | JwtBearer |

Override any key on the command line, e.g. `dotnet run --project src/MyFhirApi.Api -- --Smart:Enabled=true`.

## Extension points

```mermaid
flowchart LR
    new["New resource type<br/>(e.g. Encounter)"] --> s1["SupportedResources.cs<br/>add ResourceDefinition + search params"]
    s1 --> s2["PatientCompartment.cs<br/>add a rule (if it's patient data)"]
    s2 --> auto["Picked up automatically by:<br/>routing, storage, indexing,<br/>/metadata, sample data import"]

    newparam["New search parameter<br/>(existing type)"] --> p1["SupportedResources.cs<br/>add SearchParameterDefinition"]
    p1 --> p2["Re-save existing resources<br/>to index them (no reindex job yet)"]

    newtype["New parameter TYPE<br/>(quantity, number, uri...)"] --> t1["SearchParamKind enum"]
    t1 --> t2["SearchIndexer: extract values"]
    t2 --> t3["SearchIndexEntity: columns"]
    t3 --> t4["SearchService: Match... method"]
    t4 --> t5["MetadataController: type mapping"]
```
