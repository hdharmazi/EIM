## MODIFIED Requirements

### Requirement: API validates bearer tokens and resolves the user
`ARM.Integration.Dashboard.API` SHALL validate Entra JWT bearer tokens, resolve the caller to a dashboard user (by email), and expose their roles and validation status. Bearer token validation SHALL be the only supported authentication mechanism; the API SHALL NOT accept an API key or any other credential that bypasses Entra. Inactive or unknown users SHALL be denied.

#### Scenario: Valid token accepted
- **WHEN** a request carries a valid Entra bearer token for an active dashboard user
- **THEN** the API authorizes the request and resolves the user's role

#### Scenario: Inactive or unknown user denied
- **WHEN** a request carries a valid token for a user who is inactive or not present in the users table
- **THEN** the API responds 403

#### Scenario: API key is no longer accepted
- **WHEN** a caller sends a request carrying an `X-Api-Key` header and no bearer token
- **THEN** the API rejects the request as unauthenticated

#### Scenario: Every caller resolves to a dashboard user
- **WHEN** any request is authorized
- **THEN** the caller has been resolved to an active row in the users table, with no authorization path that skips that resolution
