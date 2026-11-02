# 3. Class diagrams

Notation: `+` public, `-` private, `$` static. `Task~T~` means `Task<T>`.
Each diagram shows one folder, plus the classes it talks to directly.

## Overview: who depends on whom

```mermaid
classDiagram
    direction LR
    class FhirController
    class MetadataController
    class SmartConfigController
    class SmartAccessService
    class ResourceRepository
    class SearchService
    class SearchIndexer
    class BundleBuilder
    class SupportedResources
    class FhirDbContext
    class SampleDataLoader

    FhirController --> SmartAccessService : authorize
    FhirController --> ResourceRepository : CRUD + history
    FhirController --> SearchService : search
    FhirController --> BundleBuilder : build Bundles
    FhirController ..> SupportedResources : validate type
    MetadataController ..> SupportedResources : describe
    SmartConfigController ..> SmartAccessService : shares SmartOptions
    ResourceRepository --> FhirDbContext
    ResourceRepository ..> SearchIndexer : index on write
    SearchService --> FhirDbContext
    SearchService ..> SupportedResources
    SearchIndexer ..> SupportedResources
    SampleDataLoader --> ResourceRepository
    SampleDataLoader --> FhirDbContext
```

Solid arrows are injected dependencies (constructor). Dotted arrows are calls to static classes.

---

## Controllers/

```mermaid
classDiagram
    class ControllerBase {
        <<ASP.NET Core>>
    }
    class FhirController {
        -ResourceRepository repository
        -SearchService search
        -SmartAccessService smart
        +Create(type, Resource body) Task~IActionResult~
        +Read(type, id) Task~IActionResult~
        +Update(type, id, Resource body) Task~IActionResult~
        +Delete(type, id) Task~IActionResult~
        +History(type, id) Task~IActionResult~
        +VRead(type, id, versionId) Task~IActionResult~
        +Search(type) Task~IActionResult~
        -ResourceResult(Resource, status) ObjectResult
        -ValidBody(type, Resource) Resource
        -ParseIfMatch() int?
        -string BaseUrl
    }
    class MetadataController {
        -IOptions~SmartOptions~ smartOptions
        +Get() IActionResult
        -Describe(ResourceDefinition)$ ResourceComponent
        -Security() SecurityComponent
    }
    class SmartConfigController {
        -IOptions~SmartOptions~ options
        +Get() IActionResult
    }
    ControllerBase <|-- FhirController
    ControllerBase <|-- MetadataController
    ControllerBase <|-- SmartConfigController
```

| Route | Action | Returns |
|---|---|---|
| `POST /{type}` | `Create` | 201 + resource |
| `GET /{type}/{id}` | `Read` | 200 + resource |
| `PUT /{type}/{id}` | `Update` | 200, or 201 if created |
| `DELETE /{type}/{id}` | `Delete` | 204 |
| `GET /{type}/{id}/_history` | `History` | 200 + Bundle(history) |
| `GET /{type}/{id}/_history/{vid}` | `VRead` | 200 + resource |
| `GET /{type}?...` | `Search` | 200 + Bundle(searchset) |
| `GET /metadata` | `MetadataController.Get` | 200 + CapabilityStatement |
| `GET /.well-known/smart-configuration` | `SmartConfigController.Get` | 200 + plain JSON |

---

## Fhir/ (FHIR plumbing)

```mermaid
classDiagram
    class SupportedResources {
        <<static>>
        +IReadOnlyDictionary All$
        +Get(typeName)$ ResourceDefinition
    }
    class ResourceDefinition {
        <<record>>
        +string TypeName
        +Type PocoType
        +IReadOnlyList~SearchParameterDefinition~ SearchParameters
        +IReadOnlyList~string~ Includes
        +FindParameter(name) SearchParameterDefinition?
    }
    class FhirException {
        +HttpStatusCode Status
        +IssueType IssueType
        +BadRequest(msg)$ FhirException
        +NotFound(msg)$ FhirException
        +Gone(msg)$ FhirException
        +PreconditionFailed(msg)$ FhirException
        +Unauthorized(msg)$ FhirException
        +Forbidden(msg)$ FhirException
    }
    class Exception {
        <<.NET>>
    }
    class FhirExceptionFilter {
        +OnException(ExceptionContext)
    }
    class IExceptionFilter {
        <<interface>>
    }
    class OperationOutcomeFactory {
        <<static>>
        +Create(severity, code, messages)$ OperationOutcome
        +Error(code, messages)$ OperationOutcome
    }
    class FhirJsonInputFormatter {
        +ReadRequestBodyAsync(context, encoding) Task~InputFormatterResult~
        #CanReadType(Type) bool
    }
    class FhirJsonOutputFormatter {
        +WriteResponseBodyAsync(context, encoding) Task
        #CanWriteType(Type) bool
    }
    class TextInputFormatter {
        <<ASP.NET Core>>
    }
    class TextOutputFormatter {
        <<ASP.NET Core>>
    }
    class FhirJson {
        <<static>>
        +Serialize(Base)$ string
        +Parse(json)$ Resource
        +ParseAs(json)$ T
    }

    SupportedResources "1" *-- "many" ResourceDefinition
    ResourceDefinition "1" *-- "many" SearchParameterDefinition
    Exception <|-- FhirException
    IExceptionFilter <|.. FhirExceptionFilter
    FhirExceptionFilter ..> FhirException : catches
    FhirExceptionFilter ..> OperationOutcomeFactory : builds response
    TextInputFormatter <|-- FhirJsonInputFormatter
    TextOutputFormatter <|-- FhirJsonOutputFormatter
```

`SupportedResources.All` is a dictionary from type name to `ResourceDefinition`. `FhirJson.ParseAs` stands for the generic `Parse<T>`.

`FhirJsonInputFormatter`, `FhirJsonOutputFormatter` and `FhirJson` all wrap the Firely SDK's
`FhirJsonDeserializer` / `FhirJsonSerializer`. FHIR JSON has rules plain System.Text.Json doesn't know, such as
`resourceType`, choice types like `effectiveDateTime`, and primitive extensions in `_field`.

---

## Data/

```mermaid
classDiagram
    class DbContext {
        <<EF Core>>
    }
    class FhirDbContext {
        +DbSet~ResourceEntity~ Resources
        +DbSet~SearchIndexEntity~ SearchIndex
        #OnModelCreating(ModelBuilder)
    }
    class ResourceEntity {
        +long Pk
        +string ResourceType
        +string ResourceId
        +int VersionId
        +DateTimeOffset LastUpdated
        +bool IsCurrent
        +bool IsDeleted
        +string? Json
    }
    class SearchIndexEntity {
        +long Id
        +string ResourceType
        +string ResourceId
        +string ParamName
        +string? StringValue
        +string? StringExact
        +string? TokenSystem
        +string? TokenCode
        +DateTimeOffset? DateStart
        +DateTimeOffset? DateEnd
        +string? ReferenceValue
    }
    class ResourceRepository {
        -FhirDbContext db
        -TimeProvider clock
        +CreateAsync(Resource) Task~Resource~
        +UpdateAsync(Resource, int? ifMatchVersion) Task~ResourceAndCreatedFlag~
        +DeleteAsync(type, id) Task
        +ReadAsync(type, id) Task~Resource~
        +VReadAsync(type, id, versionId) Task~Resource~
        +HistoryAsync(type, id) Task~List~ResourceEntity~~
        +ExistsAsync(type, id) Task~bool~
        -CurrentAsync(type, id) Task~ResourceEntity?~
        -WriteVersionAsync(Resource, versionId, previous) Task
        -ValidateAsync(Resource) Task
    }
    class SampleDataLoader {
        -FhirDbContext db
        -ResourceRepository repository
        +LoadIfEmptyAsync(folder) Task
        +LoadBundleAsync(Bundle) Task~LoadedAndSkippedCounts~
    }

    DbContext <|-- FhirDbContext
    FhirDbContext "1" o-- "many" ResourceEntity : Resources table
    FhirDbContext "1" o-- "many" SearchIndexEntity : SearchIndex table
    ResourceRepository --> FhirDbContext
    ResourceRepository ..> SearchIndexer : BuildIndex()
    ResourceRepository ..> FhirJson : Serialize/Parse
    SampleDataLoader --> ResourceRepository
    ResourceEntity .. SearchIndexEntity : same ResourceType and ResourceId, no FK
```

`UpdateAsync` returns the tuple `(Resource Resource, bool Created)` and `LoadBundleAsync` returns `(int Loaded, int Skipped)`.
Mermaid can't draw tuples, hence the made-up names above.

There's **no foreign key** between the two tables on purpose. A resource has many versions, but only the current
one has index rows, so the index points at a resource id, not at a specific version row.

---

## Search/

```mermaid
classDiagram
    class SearchParamKind {
        <<enumeration>>
        String
        Token
        Date
        Reference
    }
    class SearchParameterDefinition {
        <<record>>
        +string Name
        +SearchParamKind Type
        +string Expression
        +string? TargetType
    }
    class SearchCriterion {
        <<record>>
        +string Name
        +string? Modifier
        +string Value
    }
    class SearchRequest {
        +List~SearchCriterion~ Criteria
        +List~string~ Includes
        +int Count
        +int Offset
        +Parse(IQueryCollection)$ SearchRequest
        +ToQueryString(offset) string
    }
    class SearchResult {
        <<record>>
        +IReadOnlyList~Resource~ Matches
        +IReadOnlyList~Resource~ Included
        +int Total
        +IReadOnlyList~string~ Warnings
    }
    class SearchService {
        -FhirDbContext db
        +SearchAsync(type, SearchRequest) Task~SearchResult~
        -MatchingIds(type, param, modifier, value) IQueryable~string~
        -MatchString(index, modifier, value)$
        -MatchToken(index, value)$
        -MatchDate(index, value)$
        -MatchReference(index, param, value)$
        -IncludesAsync(...) Task~List~Resource~~
    }
    class SearchIndexer {
        <<static>>
        +BuildIndex(Resource)$ List~SearchIndexEntity~
        +Normalize(string)$ string
        -StringValues(Base)$
        -TokenValues(Base)$
        -DateValues(Base)$
        -ReferenceValues(Base, targetType)$
    }
    class FhirDateRange {
        <<record struct>>
        +DateTimeOffset Start
        +DateTimeOffset End
        +TryParse(value, out range)$ bool
    }
    class BundleBuilder {
        <<static>>
        +SearchSet(baseUrl, type, SearchRequest, SearchResult)$ Bundle
        +History(baseUrl, versions)$ Bundle
    }

    SearchParameterDefinition --> SearchParamKind
    SearchRequest "1" *-- "many" SearchCriterion
    SearchService ..> SearchRequest : reads
    SearchService ..> SearchResult : returns
    SearchService ..> SearchParameterDefinition : looks up by name
    SearchService ..> FhirDateRange : parses search dates
    SearchIndexer ..> SearchParameterDefinition : evaluates Expression
    SearchIndexer ..> FhirDateRange : parses stored dates
    BundleBuilder ..> SearchResult
    BundleBuilder ..> SearchRequest : paging links
```

The same `FhirDateRange` is used on **both** sides: when indexing a stored date and when parsing a date in the
query. That's what makes "`birthdate=1984`" match a stored `1984-03-12`. See [Search engine](05-search-engine.md).

---

## Auth/

```mermaid
classDiagram
    class FhirInteraction {
        <<enumeration>>
        Create
        Read
        Update
        Delete
        Search
    }
    class Contexts {
        <<enumeration>>
        Patient
        User
        System
    }
    class SmartScope {
        <<record>>
        +Contexts Context
        +string ResourceType
        +string Permissions
        +TryParse(raw, out scope)$ bool
        +Allows(resourceType, FhirInteraction) bool
    }
    class SmartOptions {
        +bool Enabled
        +string AuthorizeEndpoint
        +string TokenEndpoint
    }
    class SmartAccessService {
        -IOptions~SmartOptions~ options
        +Authorize(ClaimsPrincipal, resourceType, FhirInteraction) AccessDecision
        +GetScopes(ClaimsPrincipal)$ IEnumerable~SmartScope~
    }
    class AccessDecision {
        <<record>>
        +string? PatientId
        +AccessDecision Unrestricted$
        +EnsureCanAccess(Resource)
    }
    class PatientCompartment {
        <<static>>
        +Contains(Resource, patientId)$ bool
        +SearchCriterion(resourceType, patientId)$ NameAndValue
    }

    SmartScope --> Contexts
    SmartScope ..> FhirInteraction
    SmartAccessService --> SmartOptions
    SmartAccessService ..> SmartScope : parses token scopes
    SmartAccessService ..> AccessDecision : returns
    AccessDecision ..> PatientCompartment : checks
```

`Contexts` is nested inside `SmartScope` in the code (`SmartScope.Contexts`).

---

## The Firely model classes we use

These come from the `Hl7.Fhir.R4` NuGet package, not from this repo. Simplified.

```mermaid
classDiagram
    class Base {
        <<Firely>>
    }
    class Resource {
        <<Firely>>
        +string Id
        +Meta Meta
        +string TypeName
    }
    class DomainResource {
        <<Firely>>
    }
    class Meta {
        +string VersionId
        +DateTimeOffset? LastUpdated
    }
    class Patient {
        +List~Identifier~ Identifier
        +List~HumanName~ Name
        +AdministrativeGender? Gender
        +string BirthDate
    }
    class Observation {
        +ObservationStatus? Status
        +List~CodeableConcept~ Category
        +CodeableConcept Code
        +ResourceReference Subject
        +DataType Effective
        +DataType Value
    }
    class Bundle {
        +BundleType? Type
        +int? Total
        +List~LinkComponent~ Link
        +List~EntryComponent~ Entry
    }
    class OperationOutcome {
        +List~IssueComponent~ Issue
    }
    class CapabilityStatement
    class ResourceReference {
        +string Reference
    }

    Base <|-- Resource
    Resource <|-- DomainResource
    Resource <|-- Bundle
    DomainResource <|-- Patient
    DomainResource <|-- Observation
    DomainResource <|-- OperationOutcome
    DomainResource <|-- CapabilityStatement
    Resource *-- Meta
    Observation *-- ResourceReference : subject
    ResourceReference ..> Patient : points to
```

`Observation.Effective` and `Observation.Value` are FHIR **choice types** (`effective[x]`, `value[x]`).
In JSON they show up as `effectiveDateTime` / `effectivePeriod`, `valueQuantity` / `valueString`, and so on.
