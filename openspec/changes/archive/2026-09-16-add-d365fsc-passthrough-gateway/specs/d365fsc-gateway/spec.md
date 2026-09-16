## ADDED Requirements

### Requirement: Versioned route surface
The gateway SHALL expose exactly two route families under a version segment: data entities and
bound actions at `/api/v1/d365fsc/{environment}/data/{*path}`, and custom services at
`/api/v1/d365fsc/{environment}/services/{group}/{service}/{operation}`. Any other path SHALL return
404. The data route SHALL accept GET, POST and PATCH. The services route SHALL accept POST only.

#### Scenario: Data entity route accepted
- **WHEN** a request arrives for `GET /api/v1/d365fsc/DEV/data/CustomersV3`
- **THEN** the gateway treats it as a data-entity request with resource `CustomersV3`

#### Scenario: Custom service route accepted
- **WHEN** a request arrives for `POST /api/v1/d365fsc/DEV/services/WTCServices/CustomerService/CreateCustomer`
- **THEN** the gateway treats it as a service request targeting D365 `api/services/WTCServices/CustomerService/CreateCustomer`

#### Scenario: Unsupported verb on the services route
- **WHEN** a `GET` is issued against a `/services/` route
- **THEN** the gateway returns 405

#### Scenario: Unrecognised route
- **WHEN** a request arrives for a path outside both route families
- **THEN** the gateway returns 404 without contacting D365

### Requirement: Environment resolution from the URL
The environment SHALL be taken from the URL path segment, matched case-insensitively against
`ENVIRONMENTTABLE.ENVIRONMENT`, and normalised to upper case for all logging and metric dimensions.
There SHALL be no default environment.

#### Scenario: Lower-case environment resolves
- **WHEN** a caller requests `/api/v1/d365fsc/dev/data/CustomersV3` and a row exists for `DEV`
- **THEN** the row is resolved and the request proceeds

#### Scenario: Environment not configured
- **WHEN** the requested environment has no matching `ENVIRONMENTTABLE` row for the consumer's system profile
- **THEN** the gateway returns 404 and no D365 call is made

### Requirement: Connection profile resolved server-side
The `SYSTEM` used for the `ENVIRONMENTTABLE` lookup SHALL come from
`GATEWAYCONSUMER.SYSTEMPROFILE` and SHALL NOT be derived from any caller-supplied value.

#### Scenario: Profile taken from the consumer record
- **WHEN** a request is authorized for consumer `acme` whose `SYSTEMPROFILE` is `D365FSC-GW`
- **THEN** the connection is read from `ENVIRONMENTTABLE(SYSTEM='D365FSC-GW', ENVIRONMENT=<env>)`

#### Scenario: Caller cannot select a different profile
- **WHEN** a caller supplies a system or profile value in the path, query string or any header
- **THEN** the value is ignored and the consumer record's profile is used

### Requirement: Transparent request forwarding
The gateway SHALL forward the request method, resource path and raw query string to D365 without
parsing or re-serialising the query string, and SHALL forward the request body unmodified. The
gateway SHALL NOT inject query parameters that the caller did not supply, including
`cross-company`.

#### Scenario: Query string preserved verbatim
- **WHEN** a caller sends a `$skiptoken` value containing percent-encoded quotes and commas
- **THEN** the query string reaches D365 byte-for-byte as received

#### Scenario: Cross-company not injected
- **WHEN** a caller omits `cross-company`
- **THEN** the gateway forwards the request without it and D365 applies its default company scoping

### Requirement: Header allow-lists in both directions
The gateway SHALL forward only an explicit allow-list of request headers to D365 and return only an
explicit allow-list of response headers to the caller. Caller-supplied `Authorization`,
`x-functions-key`, `Host` and `X-Consumer-Id` headers SHALL NOT be forwarded to D365.

#### Scenario: Conditional request headers forwarded
- **WHEN** a caller sends `If-Match`, `Accept`, `Content-Type` or `Prefer`
- **THEN** those headers are forwarded to D365

#### Scenario: Credential headers stripped
- **WHEN** a caller sends an `Authorization` header
- **THEN** it is discarded and replaced by the gateway's own bearer token for the resolved environment

### Requirement: Canonical resource spelling forwarded
Resource names SHALL be matched case-insensitively but forwarded to D365 using the spelling stored
in `GATEWAYPERMISSION.RESOURCE`.

#### Scenario: Mis-cased entity name corrected
- **WHEN** a caller requests `data/customersv3` and the granted resource is stored as `CustomersV3`
- **THEN** the gateway forwards `data/CustomersV3` to D365

### Requirement: Response link rewriting
The gateway SHALL rewrite absolute D365 URLs in the `@odata.nextLink` response property and in the
`Location` response header so that they address the gateway, preserving the query string and the
environment segment.

#### Scenario: Paging link points at the gateway
- **WHEN** D365 returns `@odata.nextLink` addressed to the D365 host
- **THEN** the caller receives a link addressed to `{gatewayBaseUrl}/api/v1/d365fsc/{ENV}/data/…` with the `$skiptoken` intact

#### Scenario: Created resource location rewritten
- **WHEN** D365 returns 201 with a `Location` header addressed to the D365 host
- **THEN** the returned `Location` addresses the gateway

#### Scenario: Following a rewritten link succeeds
- **WHEN** a caller issues a GET against a previously returned `@odata.nextLink`
- **THEN** the next page is returned, subject to the same authorization checks as the first page

### Requirement: Gateway base URL from configuration
The base URL used when rewriting links SHALL be read from application configuration and SHALL NOT be
derived from request headers such as `Host` or `X-Forwarded-Host`.

#### Scenario: Spoofed host header ignored
- **WHEN** a caller sends a forged `X-Forwarded-Host`
- **THEN** rewritten links still address the configured gateway base URL

### Requirement: Per-environment client caching
The gateway SHALL cache the authenticated D365 client keyed on a hash of the resolved configuration
JSON, so that a configuration change produces a new client without explicit cache invalidation, and
so that MSAL token caching survives across requests on an instance.

#### Scenario: Configuration change takes effect
- **WHEN** an administrator edits the `ENVIRONMENTTABLE` row for an environment
- **THEN** the next request for that environment uses the new configuration without a restart or a flush operation

#### Scenario: Token reused across requests
- **WHEN** consecutive requests for the same environment are served by the same instance
- **THEN** a new AAD token is not acquired for each request

### Requirement: Request payload size limit
The gateway SHALL reject request bodies exceeding a configured maximum size with 413 before
contacting D365.

#### Scenario: Oversized body rejected
- **WHEN** a caller POSTs a body larger than the configured limit
- **THEN** the gateway returns 413 and no D365 call is made

### Requirement: Content type required with a body
The gateway SHALL reject a request that carries a body without a `Content-Type` header, rather than
forwarding it or assuming a type on the caller's behalf.

#### Scenario: Body without a content type
- **WHEN** a caller sends a request with a body and no `Content-Type` header
- **THEN** the gateway returns 400 and no D365 call is made

#### Scenario: Content type is not inferred
- **WHEN** a caller sends a body with a non-JSON content type
- **THEN** the gateway forwards that content type unchanged rather than substituting JSON

### Requirement: Error responses and correlation
Every response SHALL carry a correlation identifier that is also emitted to telemetry. Error
responses SHALL NOT disclose D365 internals, the resolved environment configuration, or which
resources exist.

#### Scenario: Correlation returned to the caller
- **WHEN** any request completes, successfully or not
- **THEN** the response includes a correlation identifier that can be used to locate the telemetry record

#### Scenario: Upstream failure surfaced without detail
- **WHEN** D365 returns a server error
- **THEN** the gateway returns an error response carrying the status and correlation identifier, without the upstream body's internal detail
