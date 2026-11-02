# 6. Security: SMART on FHIR

Code: [`Auth/`](../src/MyFhirApi.Api/Auth). Configuration: `Smart:Enabled` (off in Development, on everywhere else).

## Two separate jobs

```mermaid
flowchart LR
    tok["Bearer token"] --> authn
    subgraph authn["1. Authentication (ASP.NET Core JwtBearer)"]
        a1["Is the signature valid?<br/>Expired? Right audience/issuer?"]
    end
    authn -- "HttpContext.User<br/>(claims: scope, patient, sub)" --> authz
    subgraph authz["2. Authorization (our code: SmartAccessService)"]
        b1["Does a scope allow this<br/>interaction on this type?"]
        b2["Is the resource inside the<br/>patient compartment?"]
    end
    authz --> ok["allowed: AccessDecision"]
```

JwtBearer never rejects a request by itself, because no endpoint has `[Authorize]`. It only fills in `User`.
Our code decides, so every denial comes back as a FHIR `OperationOutcome`.

## Scope syntax

```text
 patient / Observation . rs ? category=laboratory
 ───┬───   ─────┬─────   ─┬   ──────────┬─────────
    │           │         │             └─ v2 filter (ignored in this prototype → grants a bit more)
    │           │         └─ permissions: v1 read | write | *   or   v2 any of c r u d s
    │           └─ resource type, or * for all
    └─ context: patient (one patient) | user (what the user may see) | system (backend service)
```

How permissions map to interactions (`SmartScope.TryParse` + `Allows`):

| Permission | c (create) | r (read, vread, history) | u (update) | d (delete) | s (search) |
|---|:-:|:-:|:-:|:-:|:-:|
| v1 `read` | | ✔ | | | ✔ |
| v1 `write` | ✔ | | ✔ | ✔ | |
| v1 `*` | ✔ | ✔ | ✔ | ✔ | ✔ |
| v2 `rs` | | ✔ | | | ✔ |
| v2 `cruds` | ✔ | ✔ | ✔ | ✔ | ✔ |

Non-resource scopes like `openid`, `fhirUser`, `launch/patient` and `offline_access` are ignored here. They matter to
the authorization server, not to the API.

## The authorization decision — `SmartAccessService.Authorize`

```mermaid
flowchart TD
    start(["Authorize(user, type, interaction)"]) --> en{"Smart:Enabled?"}
    en -- no --> unr1(["Unrestricted<br/>(local learning mode)"])
    en -- yes --> auth{"user authenticated?<br/>(valid token)"}
    auth -- no --> e401(["401 + WWW-Authenticate: Bearer"])
    auth -- yes --> scopes["read every 'scope' / 'scp' claim<br/>split on spaces, parse each"]
    scopes --> grant{"any scope allows<br/>this type + interaction?"}
    grant -- no --> e403a(["403: No scope grants X on Type"])
    grant -- yes --> ctx{"any granting scope is<br/>user/ or system/?"}
    ctx -- yes --> unr2(["Unrestricted"])
    ctx -- "no, only patient/" --> pc{"token has a<br/>'patient' claim?"}
    pc -- no --> e403b(["403: patient/ scopes need a patient claim"])
    pc -- yes --> restricted(["AccessDecision(PatientId)<br/>→ compartment rules apply"])
```

## What a restricted (patient) decision does per interaction

```mermaid
flowchart LR
    d["AccessDecision<br/>PatientId = P"] --> rd["read / vread"]
    d --> sr["search"]
    d --> wr["create / update"]
    d --> dl["delete / history"]
    rd --> rd2["load, then EnsureCanAccess<br/>outside → 403"]
    sr --> sr2["add a criterion before querying:<br/>Patient → _id=P<br/>Observation → patient=Patient/P"]
    wr --> wr2["EnsureCanAccess on the body<br/>can't create data for another patient"]
    dl --> dl2["read current first,<br/>EnsureCanAccess, then proceed"]
```

The patient compartment rules ([`PatientCompartment.cs`](../src/MyFhirApi.Api/Auth/PatientCompartment.cs)):

| Resource | Belongs to patient P when |
|---|---|
| Patient | `id == P` |
| Observation | `subject.reference == "Patient/P"` |

The full R4 definition lists which fields link each resource type to a patient:
https://hl7.org/fhir/R4/compartmentdefinition-patient.html

## Outcomes with real tokens

What you get with the three tokens from the earlier demo:

| Request | no token | `user/*.cruds` | `user/Patient.read` | `patient/*.rs` (James) |
|---|---|---|---|---|
| `GET /metadata` | 200 | 200 | 200 | 200 |
| `GET /Patient` | 401 | 200 (all) | 200 (all) | 200 (only James) |
| `GET /Patient/{maria}` | 401 | 200 | 200 | **403** |
| `POST /Patient` | 401 | 201 | **403** | **403** (no `c`) |
| `GET /Observation` | 401 | 200 (all) | **403** (wrong type) | 200 (only James's 3) |

## Public endpoints

`GET /metadata` and `GET /.well-known/smart-configuration` never check tokens. Apps call them **before**
they have a token, to find out how to get one.

## From prototype to production

| Here | Production |
|---|---|
| `dotnet user-jwts` signs tokens with a local key from user secrets | A real authorization server; set `Authentication:Schemes:Bearer:Authority` so JwtBearer downloads its signing keys |
| `Smart:AuthorizeEndpoint` / `TokenEndpoint` are placeholders | The authorization server's real URLs (advertised in `/metadata` and SMART discovery) |
| v2 scope filters (`?category=...`) ignored | Apply them as extra search criteria |
| Compartment rules for 2 types | The full patient CompartmentDefinition |
| No audit trail | `AuditEvent` per access (HIPAA) |
