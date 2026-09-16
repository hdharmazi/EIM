## ADDED Requirements

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
