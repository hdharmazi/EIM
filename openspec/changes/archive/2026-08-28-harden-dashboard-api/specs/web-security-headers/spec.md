## ADDED Requirements

### Requirement: Browser responses carry security headers
The browser-facing application SHALL send `Strict-Transport-Security`, `X-Content-Type-Options`, `X-Frame-Options`, and `Referrer-Policy` on its responses.

#### Scenario: Headers present on the application shell
- **WHEN** a browser requests the application
- **THEN** the response carries all four security headers

#### Scenario: Headers present on static assets
- **WHEN** a browser requests a static asset such as a script or stylesheet
- **THEN** the response carries the same security headers as the application shell

### Requirement: Transport security is introduced reversibly
`Strict-Transport-Security` SHALL be introduced with a short duration and SHALL NOT request preload in its initial form, so the change can be reversed if an unforeseen dependency on plain HTTP is discovered.

#### Scenario: Initial duration is short
- **WHEN** transport security is first enabled
- **THEN** its duration is short enough that a browser's cached policy expires within a normal support window

#### Scenario: Preload is not requested initially
- **WHEN** transport security is first enabled
- **THEN** the response does not request preload

### Requirement: HTTPS is enforced before the application
Requests to the API over plain HTTP SHALL be rejected or redirected by the hosting platform, rather than relying on application middleware behind a terminating proxy.

#### Scenario: Plain HTTP request does not reach the application
- **WHEN** a request is made to the API over plain HTTP
- **THEN** it is rejected or redirected by the platform before reaching application code
