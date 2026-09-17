# gateway-access-control Specification

## Purpose
TBD - created by archiving change add-d365fsc-passthrough-gateway. Update Purpose after archive.
## Requirements
### Requirement: Function key authentication
Every gateway route SHALL require a function-level Azure Functions host key. The master key SHALL
NOT be issued to consumers. Keys SHALL be presented in the `x-functions-key` header; a key supplied
in the query string SHALL be rejected.

#### Scenario: Valid key accepted
- **WHEN** a request presents a valid function-level key in the `x-functions-key` header
- **THEN** the request proceeds to identity resolution

#### Scenario: Missing or invalid key
- **WHEN** a request presents no key or an invalid key
- **THEN** the Functions host rejects it with 401 and application code is not invoked

#### Scenario: Key in the query string rejected
- **WHEN** a request supplies the key via a `code` query-string parameter
- **THEN** the gateway returns 400, because query strings are recorded in request telemetry and intermediate logs

### Requirement: Consumer identity resolution
The gateway SHALL require an `X-Consumer-Id` header and resolve it case-insensitively against
`GATEWAYCONSUMER`. Where `GATEWAYCONSUMER.KEYHASH` is populated, the SHA-256 hash of the presented
function key MUST match it. Where `KEYHASH` is null, the identity SHALL be accepted unverified and
the request SHALL be recorded as having unverified identity.

#### Scenario: Header missing
- **WHEN** a request omits `X-Consumer-Id`
- **THEN** the gateway returns 400

#### Scenario: Unknown consumer
- **WHEN** `X-Consumer-Id` does not match any `GATEWAYCONSUMER` row
- **THEN** the gateway returns 403

#### Scenario: Key hash mismatch
- **WHEN** the consumer's `KEYHASH` is populated and the presented key's hash does not match
- **THEN** the gateway returns 403

#### Scenario: Unverified identity recorded
- **WHEN** the consumer's `KEYHASH` is null and identity is accepted from the header alone
- **THEN** telemetry for the request records the identity as unverified

### Requirement: Consumer enablement and expiry
A consumer SHALL be rejected when `ENABLED` is false or when `EXPIRESON` is in the past.

#### Scenario: Disabled consumer
- **WHEN** a request presents a consumer whose `ENABLED` is false
- **THEN** the gateway returns 403 and no D365 call is made

#### Scenario: Expired consumer
- **WHEN** a request presents a consumer whose `EXPIRESON` has passed
- **THEN** the gateway returns 403

### Requirement: Deny-by-default authorization
Access SHALL be granted only by an exact matching row in `GATEWAYPERMISSION` on
`(CONSUMERID, ENVIRONMENT, RESOURCETYPE, RESOURCE, VERB)`. There SHALL be no wildcard values and no
implicit grants. Authorization SHALL be evaluated before the environment's D365 credentials are
resolved.

#### Scenario: Granted request proceeds
- **WHEN** a matching permission row exists for the consumer, environment, resource type, resource and verb
- **THEN** the request is forwarded to D365

#### Scenario: No matching grant
- **WHEN** no matching permission row exists
- **THEN** the gateway returns 403 with no indication of whether the resource exists

#### Scenario: Verb not granted
- **WHEN** a consumer holds a GET grant on a resource and issues a POST against it
- **THEN** the gateway returns 403

#### Scenario: Environment not granted
- **WHEN** a consumer holds grants only for a non-production environment and targets a production environment
- **THEN** the gateway returns 403 before any production credential is loaded

### Requirement: Bound actions authorized separately from their entity
A bound action SHALL be authorized as `RESOURCETYPE = ACTION` with the resource expressed as
`<entity>/<action>`, independently of any `DATA` grant on the same entity.

#### Scenario: Read grant does not confer action rights
- **WHEN** a consumer holds `DATA`/`SalesOrderHeadersV2`/GET and invokes the `Confirm` bound action
- **THEN** the gateway returns 403

### Requirement: DELETE not grantable
`DELETE` SHALL be modelled in the permission schema but SHALL NOT be grantable through any
interface in this release.

#### Scenario: DELETE attempt
- **WHEN** a caller issues a DELETE against a data route
- **THEN** the gateway returns 403

### Requirement: Batch requests rejected
The gateway SHALL reject `$batch` requests, because a batch body carries sub-requests that the
authorization model does not inspect. Rejection SHALL be reported as a bad request rather than as a
forbidden one, since no grant can ever enable it.

#### Scenario: Batch endpoint blocked
- **WHEN** a caller posts to a path resolving to `$batch`
- **THEN** the gateway returns 400 and no request reaches D365

### Requirement: Strict path canonicalisation
Resource paths SHALL be canonicalised before authorization and SHALL be parsed strictly, rejecting
any path that does not match an expected shape rather than extracting a resource name on a
best-effort basis.

#### Scenario: Encoded separator rejected
- **WHEN** a path contains a percent-encoded path separator intended to disguise the targeted resource
- **THEN** the gateway returns 400 and no authorization decision is made on the decoded form

#### Scenario: Traversal and duplicate separators rejected
- **WHEN** a path contains traversal segments or duplicated separators
- **THEN** the gateway returns 400

#### Scenario: Unparseable path rejected
- **WHEN** a path cannot be resolved to a single resource and optional action
- **THEN** the gateway returns 400 rather than authorizing a partial match

### Requirement: Verified identity required before a second consumer
While any consumer exists with a null `KEYHASH`, the system SHALL NOT be operated with more than one
enabled consumer, because unverified identity ceases to be safe once more than one identity exists.

#### Scenario: Second consumer added without verification
- **WHEN** an administrator attempts to enable a second consumer while any enabled consumer has a null `KEYHASH`
- **THEN** the operation is refused with an explanation that identity verification must be enabled first

### Requirement: Grant provenance
Every `GATEWAYPERMISSION` row SHALL record the identity that created it and the time of creation.

#### Scenario: Grant attributed
- **WHEN** an administrator creates a permission grant
- **THEN** the row records the administrator's identity and a creation timestamp

### Requirement: Authorization telemetry
Every authorization denial SHALL be recorded with the consumer, environment, resource type, resource
and verb that were attempted. Denials SHALL be observable as a rate suitable for alerting.

#### Scenario: Denial diagnosable
- **WHEN** a request is denied because of a resource-name typo in a grant
- **THEN** telemetry contains the attempted five-tuple, allowing the mismatch to be identified without reproducing the request

#### Scenario: Probing detectable
- **WHEN** denials rise sharply over a short interval
- **THEN** the condition is visible as a metric that an alert rule can act on

### Requirement: Host keys reach every HTTP function in a gateway application
Every HTTP function in a gateway application SHALL perform consumer authorization, and no HTTP
function SHALL be added to a gateway application unless it does so or is harmless to every consumer
holding a key. This is required because consumer keys are Azure Functions host keys, which authorise
every HTTP function in the application rather than a single function.

#### Scenario: All HTTP functions authorize
- **WHEN** a gateway application exposes an HTTP function
- **THEN** that function authorizes the caller against the consumer's grants before performing any action

#### Scenario: A consumer key is not scoped to one route
- **WHEN** a consumer presents a valid host key to any HTTP route in the gateway application
- **THEN** the platform accepts the key, and only grant evaluation prevents the request from proceeding

### Requirement: Key verification does not affect request authorization
The gateway SHALL authorize requests solely on the stored key hash, grants, enabled state and expiry.
Whether a key was verified by the dashboard, and which application verified it, SHALL NOT be consulted
when authorizing a request.

#### Scenario: Verification metadata ignored at request time
- **WHEN** the gateway authorizes a request for a consumer
- **THEN** the decision is unchanged regardless of whether verification data is recorded for that consumer

#### Scenario: Dashboard unavailability does not affect the gateway
- **WHEN** the dashboard or its verification path is unavailable
- **THEN** gateway request authorization continues unaffected

