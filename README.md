# MyFhirApi: a FHIR R4 server you can read

A small FHIR R4 API, written by hand on .NET 10, the [Firely .NET SDK](https://docs.fire.ly/projects/Firely-NET-SDK/), EF Core and SQL Server.
It's a **learning prototype and reference**, not a production server. Each FHIR concept lives in one place in the code,
and this README maps concepts to those files.

- Resources: `Patient`, `Observation`
- Interactions: create, read, vread, update (with optimistic locking), delete, history, search, capabilities
- Search: string / token / date / reference parameters, AND/OR, modifiers, paging, `_include`
- Security: SMART on FHIR scopes (v1 and v2 syntax) and patient compartments
- Synthetic sample data in the Synthea format
- 45 tests that run against SQL Server LocalDB

**Developer docs with diagrams** (architecture, sequence and class diagrams, data model, search, security, testing): [`docs/`](docs/README.md)

---

## Quick start

Prerequisites: .NET 10 SDK and SQL Server **LocalDB** (installed with Visual Studio; check with `sqllocaldb info`).

```bash
dotnet run --project src/MyFhirApi.Api        # http://localhost:5025
dotnet test                                   # creates and drops throwaway LocalDB databases
```

On first start, the app creates the `MyFhirApi` database and loads the sample patients. Then open
[`src/MyFhirApi.Api/MyFhirApi.Api.http`](src/MyFhirApi.Api/MyFhirApi.Api.http) in Rider / Visual Studio / VS Code and run the requests one by one.
(Request 10 saves the new patient's id with a Rider response script. In other editors, copy the id from the response into `newId`.)

**Using another SQL Server:** change `ConnectionStrings:Fhir` in `appsettings.json`, e.g.
`Server=localhost;Database=MyFhirApi;Trusted_Connection=True;TrustServerCertificate=True`.

**Starting over:** drop the database and restart, and the sample data loads again:
`sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "DROP DATABASE MyFhirApi"`

---

## FHIR in 10 minutes, mapped to the code

| FHIR concept | What it means | Where it is |
|---|---|---|
| **Resource** | The unit of data: a JSON document with a `resourceType` (Patient, Observation...). The spec defines ~150 types. | Firely SDK classes (`Hl7.Fhir.Model.Patient`, ...) |
| **Supported resources** | Which types this server serves, and how they can be searched | [`Fhir/SupportedResources.cs`](src/MyFhirApi.Api/Fhir/SupportedResources.cs) |
| **REST API** | The same URL patterns for every type: `POST /Patient`, `GET /Patient/123`, `GET /Patient?name=x` ... | [`Controllers/FhirController.cs`](src/MyFhirApi.Api/Controllers/FhirController.cs) |
| **FHIR JSON** | Media type `application/fhir+json` with its own JSON rules | [`Fhir/FhirJsonFormatters.cs`](src/MyFhirApi.Api/Fhir/FhirJsonFormatters.cs) |
| **CapabilityStatement** | `GET /metadata`: the server's machine-readable contract (FHIR's equivalent of OpenAPI) | [`Controllers/MetadataController.cs`](src/MyFhirApi.Api/Controllers/MetadataController.cs) |
| **OperationOutcome** | How every error is returned: a list of issues with severity and code | [`Fhir/FhirException.cs`](src/MyFhirApi.Api/Fhir/FhirException.cs), [`Fhir/FhirExceptionFilter.cs`](src/MyFhirApi.Api/Fhir/FhirExceptionFilter.cs) |
| **Versioning** | Each change creates a new `meta.versionId`. `ETag: W/"2"`, `If-Match` for optimistic locking, `_history`, vread, and deletes that leave a "tombstone" | [`Data/ResourceRepository.cs`](src/MyFhirApi.Api/Data/ResourceRepository.cs) |
| **References** | Links between resources: `"subject": {"reference": "Patient/123"}` | Validated in `ResourceRepository.ValidateAsync` |
| **Search parameters** | Named, typed parameters defined by FHIRPath expressions, e.g. `family` → `Patient.name.family` | [`Search/SearchParameterDefinition.cs`](src/MyFhirApi.Api/Search/SearchParameterDefinition.cs) |
| **Search indexing** | Values are extracted into an index table when a resource is written | [`Search/SearchIndexer.cs`](src/MyFhirApi.Api/Search/SearchIndexer.cs) |
| **Search matching** | How each parameter type matches; AND/OR; modifiers (`:exact`, `:contains`); date prefixes (`ge`, `lt`...) | [`Search/SearchService.cs`](src/MyFhirApi.Api/Search/SearchService.cs), [`Search/FhirDateRange.cs`](src/MyFhirApi.Api/Search/FhirDateRange.cs) |
| **Bundle** | A container of resources: `searchset` for results (with paging links), `history`, `transaction` | [`Search/BundleBuilder.cs`](src/MyFhirApi.Api/Search/BundleBuilder.cs) |
| **_include** | Return referenced resources in the same Bundle (`Observation?_include=Observation:subject`) | `SearchService.IncludesAsync` |
| **Terminologies** | Codes come from code systems: LOINC for lab/vitals (`http://loinc.org|4548-4` = HbA1c), SNOMED CT, ... | Token search, sample data |
| **SMART on FHIR** | OAuth2 for health apps. Scopes like `patient/Observation.rs` say whose data and which operations | [`Auth/SmartScope.cs`](src/MyFhirApi.Api/Auth/SmartScope.cs), [`Auth/SmartAccessService.cs`](src/MyFhirApi.Api/Auth/SmartAccessService.cs) |
| **Compartment** | "Everything about patient X". `patient/` scopes are limited to it | [`Auth/PatientCompartment.cs`](src/MyFhirApi.Api/Auth/PatientCompartment.cs) |
| **Synthetic data** | Develop with fake but realistic patients, never with real PHI | [`Data/SampleDataLoader.cs`](src/MyFhirApi.Api/Data/SampleDataLoader.cs) |

### How storage works

```
POST /Patient ─► parse (Firely) ─► validate ─► assign id + meta.versionId
                                           │
              ┌────────────────────────────┴───────────────────────────┐
              ▼                                                        ▼
  Resources table (one row per VERSION)                SearchIndex table (CURRENT version only)
  ResourceType | ResourceId | VersionId | Json          ResourceId | ParamName | StringValue / Token / Date / Reference
  Patient      | 5f2a...    | 1         | {...}        5f2a...    | family    | "lopez"
  Patient      | 5f2a...    | 2         | {...}        5f2a...    | birthdate | [1984-03-12, 1984-03-13)
```

Most real FHIR servers (HAPI, Microsoft FHIR Server, Firely Server) use this "JSON document + search index" design.
FHIR resources are too deeply nested to map neatly onto relational tables.

---

## Searching: cheat sheet

| Example | Meaning |
|---|---|
| `Patient?family=smi` | String: case-insensitive *starts with* |
| `Patient?family:exact=Smith` / `family:contains=mit` | String modifiers |
| `Patient?identifier=http://example.org/fhir/mrn\|MRN-1001` | Token: `system\|code` |
| `Patient?gender=female` | Token without a system |
| `Patient?birthdate=1984` | Date: anywhere in 1984 (dates have precision) |
| `Patient?birthdate=ge1980&birthdate=lt1990` | Repeated parameter = AND |
| `Patient?name=maria,aisha` | Comma = OR |
| `Observation?patient=5f2a...` or `subject=Patient/5f2a...` | Reference |
| `Observation?code=http://loinc.org\|4548-4&_include=Observation:subject` | Include the referenced Patients |
| `Patient?_count=2&_offset=2` | Paging (follow the Bundle's `next` link) |
| `?_pretty=true` | Indented JSON |

Unknown parameters are ignored, and the Bundle includes an `OperationOutcome` warning (FHIR's default "lenient" behavior).

---

## Sample data (no production data needed)

Real production health data is never published, because it's protected health information (PHI).
The healthcare industry develops with **synthetic** data instead:

- **Bundled:** [`SampleData/synthetic-patients.json`](src/MyFhirApi.Api/SampleData/synthetic-patients.json) has 3 patients and 8 observations
  (vitals and labs with real LOINC codes). It's a Synthea-style transaction Bundle and loads automatically in Development.
- **More data:** [Synthea](https://synthea.mitre.org/downloads) publishes ready-made sets of FHIR R4 patients.
  Unzip a few patient files into `src/MyFhirApi.Api/SampleData/`, drop the database, and restart.
  Patients and Observations are imported. Other types (Encounter, Condition, Claim...) are skipped until you add them to `SupportedResources`.
  A single Synthea patient can have thousands of observations, so start with a handful of files.
- **Compare with real servers:** public test servers such as `https://hapi.fhir.org/baseR4` (HAPI) and
  `https://server.fire.ly` (Firely) let you send the same queries and compare responses. Don't put anything real there.

---

## Trying SMART auth

In Development, `Smart:Enabled` is `false`, so you can explore without tokens. To turn on scope checks:

```bash
# 1. Mint test tokens. "dotnet user-jwts" acts as a mini authorization server for local development.
dotnet user-jwts create --project src/MyFhirApi.Api --scope "user/*.cruds" --output token
dotnet user-jwts create --project src/MyFhirApi.Api --scope "patient/*.rs" \
    --claim "patient=5f2a7c1e-3b64-4c9a-9f4e-1a2b3c4d5e02" --output token

# 2. Run with scope checks on
dotnet run --project src/MyFhirApi.Api -- --Smart:Enabled=true
```

Paste a token into `@token` in the `.http` file. What to expect:

| Token | Result |
|---|---|
| none | `401` + `WWW-Authenticate: Bearer` |
| `user/Patient.read` | can read/search Patients; `POST` → `403`; `GET /Observation` → `403` |
| `user/*.cruds` | everything |
| `patient/*.rs` + `patient` claim | reads and searches only that patient's data; other patients → `403` |

`user-jwts` keeps its signing key in your user secrets (outside the repo). `appsettings.Development.json` tells JwtBearer to trust the `dotnet-user-jwts` issuer.

**For a real deployment:** use an authorization server that supports SMART (Keycloak with a SMART extension, Okta, Entra ID with custom scopes...). Set
`Authentication:Schemes:Bearer:Authority` to it, and put its URLs in `Smart:AuthorizeEndpoint` / `Smart:TokenEndpoint` so that
`/metadata` and `/.well-known/smart-configuration` advertise them. This API only *validates* tokens; it never issues them.

---

## Adding a resource type (e.g. Encounter)

1. Add an entry to [`SupportedResources.cs`](src/MyFhirApi.Api/Fhir/SupportedResources.cs) with its search parameters. Copy the FHIRPath expressions from the
   [R4 search parameter registry](https://hl7.org/fhir/R4/searchparameter-registry.html).
2. If it belongs to the patient compartment, add a rule to [`PatientCompartment.cs`](src/MyFhirApi.Api/Auth/PatientCompartment.cs).
3. Add tests. Routing, storage, indexing, `/metadata` and sample data import all pick up the new type automatically.

---

## What a production server would add

This prototype skips these on purpose. They're the natural next steps for the agency project:

- **Transaction/batch Bundles** (`POST /` with many resources in one atomic request)
- **Conditional create/update** (`If-None-Exist`, `PUT /Patient?identifier=...`)
- **Profile validation** with the Firely validator (`Firely.Fhir.Validation`) against **US Core** or your own implementation guide
- **Terminology services** (`$validate-code`, `$expand`) for checking codes
- More search: `_sort`, `_revinclude`, chained parameters (`subject:Patient.name=x`), `_summary`, `_elements`, quantity/number/uri types
- XML format, `_format`, `Prefer: return=minimal`
- **Bulk Data export** (`$export`), required for many payer and public-health use cases
- **Audit logging** (FHIR `AuditEvent`, HIPAA access logs) and **Provenance**
- EF Core **migrations** instead of `EnsureCreated`, and a search index that can be rebuilt when parameters change
- Hardening: rate limiting, HTTPS-only, CORS policy, health checks, OpenTelemetry

Before building the real thing, also look at existing servers you could use or extend:
[Microsoft FHIR Server](https://github.com/microsoft/fhir-server) (open source, .NET; the basis of Azure Health Data Services),
[Firely Server](https://fire.ly/products/firely-server/) (commercial, .NET),
and [HAPI FHIR](https://hapifhir.io/) (open source, Java).

## Project layout

```
src/MyFhirApi.Api/
  Auth/         SMART scopes, access checks, patient compartment
  Controllers/  FHIR REST endpoints, /metadata, SMART discovery
  Data/         EF Core entities, versioned repository, sample data loader
  Fhir/         FHIR JSON formatters, OperationOutcome errors, supported resources
  Search/       search parameter definitions, indexer, query engine, Bundle builder
  SampleData/   synthetic FHIR Bundles
tests/MyFhirApi.Api.Tests/   integration tests (WebApplicationFactory + LocalDB)
```
