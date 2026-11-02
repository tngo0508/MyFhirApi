# 2. Request flows (sequence diagrams)

These follow the real call order in the code. Participants:

| Short name | Class |
|---|---|
| Client | any HTTP client |
| JwtBearer | ASP.NET Core authentication middleware |
| Formatter | `FhirJsonInputFormatter` / `FhirJsonOutputFormatter` |
| Controller | `FhirController` |
| Smart | `SmartAccessService` |
| Repo | `ResourceRepository` |
| Indexer | `SearchIndexer` |
| Search | `SearchService` |
| DB | `FhirDbContext` → SQL Server |

---

## Create a Patient — `POST /Patient`

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant JwtBearer
    participant Formatter as FhirJsonInputFormatter
    participant Controller as FhirController
    participant Smart as SmartAccessService
    participant Repo as ResourceRepository
    participant Indexer as SearchIndexer
    participant DB as SQL Server

    Client->>JwtBearer: POST /Patient + Bearer token + JSON body
    JwtBearer->>JwtBearer: validate signature, expiry, audience → HttpContext.User
    JwtBearer->>Formatter: model binding for [FromBody] Resource?
    Formatter->>Formatter: Firely TryDeserializeResource(json)
    Note right of Formatter: problems → ModelState (no exception yet)
    Formatter->>Controller: Create("Patient", patient)
    Controller->>Controller: SupportedResources.Get("Patient")
    Controller->>Smart: Authorize(User, "Patient", Create)
    Smart-->>Controller: AccessDecision (unrestricted or PatientId)
    Controller->>Controller: ValidBody(): ModelState ok? body type = URL type?
    Controller->>Controller: access.EnsureCanAccess(patient)
    Controller->>Repo: CreateAsync(patient)
    Repo->>Repo: patient.Id = new Guid (client id ignored)
    Repo->>Repo: meta.versionId = 1, meta.lastUpdated = now
    Repo->>Repo: ValidateAsync: Firely Validate() + reference check
    Repo->>Indexer: BuildIndex(patient)
    Indexer-->>Repo: List of SearchIndexEntity (family, given, birthdate...)
    Repo->>DB: BEGIN TRANSACTION
    Repo->>DB: INSERT Resources (v1, IsCurrent=1, Json)
    Repo->>DB: DELETE old SearchIndex rows for this id (none yet)
    Repo->>DB: INSERT SearchIndex rows
    Repo->>DB: COMMIT
    Repo-->>Controller: patient (with id + meta)
    Controller-->>Client: 201 Created<br/>Location: /Patient/{id}/_history/1<br/>ETag: W/"1", Last-Modified
```

---

## Read with a patient-scoped token — `GET /Patient/{id}`

A patient-facing app holds a token with `patient/*.rs` and `"patient": "JAMES_ID"`.

```mermaid
sequenceDiagram
    autonumber
    actor App as Patient app (James)
    participant Controller as FhirController
    participant Smart as SmartAccessService
    participant Repo as ResourceRepository
    participant DB as SQL Server

    App->>Controller: GET /Patient/MARIA_ID  (token: patient/*.rs, patient=JAMES_ID)
    Controller->>Smart: Authorize(User, "Patient", Read)
    Smart->>Smart: scopes granting Read on Patient → only patient/ scopes
    Smart-->>Controller: AccessDecision(PatientId = JAMES_ID)
    Controller->>Repo: ReadAsync("Patient", MARIA_ID)
    Repo->>DB: SELECT current row
    DB-->>Repo: row (not deleted)
    Repo-->>Controller: Patient (Maria)
    Controller->>Controller: access.EnsureCanAccess(Maria)
    Note over Controller: PatientCompartment.Contains(Maria, JAMES_ID) = false
    Controller-->>App: 403 OperationOutcome "outside the patient context"
```

Reads check the compartment **after** loading, because you only know who a resource belongs to once you have it.
Searches narrow the query **before** running it (see below).

---

## Update with optimistic locking — `PUT /Patient/{id}` + `If-Match`

Two users edit the same patient. The second save is rejected instead of silently overwriting the first.

```mermaid
sequenceDiagram
    autonumber
    actor A as User A
    actor B as User B
    participant Controller as FhirController
    participant Repo as ResourceRepository
    participant DB as SQL Server

    A->>Controller: GET /Patient/123
    Controller-->>A: 200, ETag: W/"1"
    B->>Controller: GET /Patient/123
    Controller-->>B: 200, ETag: W/"1"

    A->>Controller: PUT /Patient/123, If-Match: W/"1"
    Controller->>Repo: UpdateAsync(patient, ifMatchVersion: 1)
    Repo->>DB: current version?
    DB-->>Repo: 1 ✔ matches
    Repo->>DB: old row IsCurrent=0, INSERT v2, rebuild index
    Controller-->>A: 200, ETag: W/"2"

    B->>Controller: PUT /Patient/123, If-Match: W/"1"
    Controller->>Repo: UpdateAsync(patient, ifMatchVersion: 1)
    Repo->>DB: current version?
    DB-->>Repo: 2 ✘ doesn't match
    Repo-->>Controller: throw FhirException 412
    Controller-->>B: 412 Precondition Failed (OperationOutcome: conflict)
    Note over B: B must re-read v2, re-apply the change, and retry with W/"2"
```

Without `If-Match` the update always succeeds ("last write wins").
A `PUT` to an id that doesn't exist yet **creates** it (201). FHIR calls this "update as create".

---

## Delete, history and vread

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Controller as FhirController
    participant Repo as ResourceRepository
    participant DB as SQL Server

    Client->>Controller: DELETE /Patient/123
    Controller->>Repo: DeleteAsync
    Repo->>DB: v2 IsCurrent=0, INSERT v3 (IsDeleted=1, Json=NULL)
    Repo->>DB: DELETE SearchIndex rows for 123
    Controller-->>Client: 204 No Content

    Client->>Controller: GET /Patient/123
    Controller->>Repo: ReadAsync
    Repo->>DB: current row → IsDeleted=1
    Controller-->>Client: 410 Gone

    Client->>Controller: GET /Patient/123/_history
    Controller->>Repo: HistoryAsync
    Repo->>DB: all versions, newest first
    Controller->>Controller: BundleBuilder.History
    Controller-->>Client: 200 Bundle(type=history)<br/>v3 DELETE, v2 PUT, v1 POST

    Client->>Controller: GET /Patient/123/_history/1
    Controller->>Repo: VReadAsync(version 1)
    Controller-->>Client: 200 the original Patient
```

Deleting a deleted resource is a no-op that still returns 204, so `DELETE` is idempotent.

---

## Search with `_include` — `GET /Observation?patient=X&code=...&_include=Observation:subject`

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Controller as FhirController
    participant Smart as SmartAccessService
    participant Req as SearchRequest
    participant Search as SearchService
    participant DB as SQL Server
    participant Bundle as BundleBuilder

    Client->>Controller: GET /Observation?patient=X&code=http://loinc.org|4548-4&_include=Observation:subject
    Controller->>Smart: Authorize(User, "Observation", Search)
    Smart-->>Controller: AccessDecision
    Controller->>Req: SearchRequest.Parse(query)
    Req-->>Controller: Criteria [patient=X, code=...], Includes [Observation:subject], Count 20, Offset 0
    opt token has only patient/ scopes
        Controller->>Controller: add criterion patient=Patient/TOKEN_PATIENT
    end
    Controller->>Search: SearchAsync("Observation", request)
    Search->>Search: build LINQ: current, not deleted,<br/>AND Id IN (index match for patient)<br/>AND Id IN (index match for code)
    Search->>DB: SELECT COUNT(*)
    Search->>DB: SELECT ... ORDER BY Pk OFFSET 0 FETCH 20
    DB-->>Search: matching Observation rows
    Search->>DB: SearchIndex: subject references of this page
    Search->>DB: Resources: those Patients (current, not deleted)
    Search-->>Controller: SearchResult(matches, included, total, warnings)
    Controller->>Bundle: SearchSet(baseUrl, type, request, result)
    Bundle-->>Controller: Bundle(searchset): links self/first/next/previous,<br/>entries mode=match, mode=include, mode=outcome (warnings)
    Controller-->>Client: 200 Bundle
```

Details of the matching logic are in [Search engine](05-search-engine.md).

---

## Error paths

How each failure becomes a response. Every error body is an `OperationOutcome`.

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Formatter as FhirJsonInputFormatter
    participant Controller as FhirController
    participant Filter as FhirExceptionFilter

    Client->>Formatter: POST /Patient  body: {not json
    Formatter->>Formatter: Firely throws JsonException → caught
    Formatter->>Controller: ModelState error "Body is not valid JSON", resource = null
    Controller->>Controller: Authorize → ok (or throw 401/403 first)
    Controller->>Controller: ValidBody → throw FhirException 400
    Controller-->>Filter: FhirException(400, "Body is not valid JSON...")
    Filter->>Filter: OperationOutcomeFactory.Error(Invalid, message)
    Filter-->>Client: 400 OperationOutcome

    Client->>Controller: GET /Encounter/1
    Controller->>Controller: SupportedResources.Get("Encounter") → throw 404
    Controller-->>Filter: FhirException(404)
    Filter-->>Client: 404 OperationOutcome "not supported by this server"

    Client->>Controller: GET /Patient (bug somewhere: NullReferenceException)
    Controller-->>Filter: unexpected exception
    Filter->>Filter: log the error
    Filter-->>Client: 500 OperationOutcome "An unexpected error occurred." (no internals leaked)
```

| Status | Thrown by | Example cause |
|---|---|---|
| 400 | `ValidBody`, `ResourceRepository.ValidateAsync`, `SearchService` | Bad JSON, invalid FHIR, unknown Observation subject, bad date in search |
| 401 | `SmartAccessService` | Access checks on and no valid token |
| 403 | `SmartAccessService`, `AccessDecision.EnsureCanAccess` | Missing scope, or outside the patient compartment |
| 404 | `SupportedResources.Get`, repository | Unknown resource type or id |
| 410 | `ResourceRepository.ReadAsync` | Resource was deleted |
| 412 | `ResourceRepository.UpdateAsync` | `If-Match` version isn't current |
| 500 | `FhirExceptionFilter` | Any unexpected exception |

---

## Startup and sample data loading

```mermaid
sequenceDiagram
    autonumber
    participant Program as Program.cs
    participant DB as SQL Server
    participant Loader as SampleDataLoader
    participant Repo as ResourceRepository

    Program->>DB: EnsureCreatedAsync()
    Program->>Loader: LoadIfEmptyAsync("SampleData")
    Loader->>DB: any Resources rows?
    alt database already has data
        Loader-->>Program: skip
    else empty
        loop each SampleData/*.json (a transaction Bundle)
            Loader->>Loader: parse Bundle with Firely
            Loader->>Loader: pass 1: id = uuid from "urn:uuid:..." fullUrl<br/>map urn:uuid:X → Patient/X
            Loader->>Loader: pass 2: Patients first, skip unsupported types (Encounter...)
            loop each supported resource
                Loader->>Loader: FHIRPath descendants() → rewrite urn:uuid references
                Loader->>Repo: UpdateAsync(resource) (PUT semantics: keeps the uuid as id)
            end
        end
        Loader-->>Program: "Loaded 11 resources (1 skipped)"
    end
```

---

## SMART on FHIR: how an app gets and uses a token

The OAuth2 part happens between the app and the authorization server. This API only joins at the last step.

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant App as SMART app
    participant API as MyFhirApi
    participant AS as Authorization server

    App->>API: GET /.well-known/smart-configuration (no token needed)
    API-->>App: authorization_endpoint, token_endpoint, capabilities
    App->>AS: redirect user to /authorize?scope=patient/*.rs launch/patient openid...
    AS->>User: log in + "Allow this app to read your records?"
    User->>AS: approve
    AS-->>App: authorization code
    App->>AS: POST /token (code + PKCE verifier)
    AS-->>App: access_token (JWT with scope + patient claim), patient id
    App->>API: GET /Observation  Authorization: Bearer TOKEN
    API->>API: JwtBearer validates signature with AS's keys
    API->>API: SmartAccessService checks scopes + patient compartment
    API-->>App: 200 Bundle with only that patient's Observations
```

Locally, `dotnet user-jwts create --scope ... --claim patient=...` skips steps 3–8 and hands you a token directly.
