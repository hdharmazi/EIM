## MODIFIED Requirements

### Requirement: Shared components are not deployed per environment
The system SHALL distinguish components deployed once and shared across non-production environments from components deployed per environment. Requesting a shared component against an environment that does not host it SHALL be rejected.

The dashboard API, the dashboard frontend and the D365 F&SC gateway SHALL be shared. Queues, storage, the Functions host and schema migrations SHALL be per environment.

#### Scenario: Per-environment component
- **WHEN** the Functions host, queues, storage, or schema is deployed
- **THEN** it is deployed to resources specific to the requested environment

#### Scenario: Shared component requested for the wrong environment
- **WHEN** a shared dashboard component is requested against an environment that does not host it
- **THEN** the script rejects the request rather than creating a second deployment

#### Scenario: Gateway requested for the wrong environment
- **WHEN** the gateway component is requested against an environment that does not host it
- **THEN** the script rejects the request rather than creating a second gateway application

#### Scenario: Adding an environment requires no gateway
- **WHEN** a new environment is added to the configuration
- **THEN** it requires its own queues, storage and Functions host, and reuses the existing shared gateway and dashboard

### Requirement: Gateway deployment component
The deployment script SHALL support a `gateway` component that provisions and deploys the D365 F&SC
pass-through gateway function app, and SHALL include it when all components are deployed. The gateway
SHALL be provisioned once for all non-production environments, under a name that does not imply a
single environment, and SHALL be deployed only from the environment configured to host it.

#### Scenario: Gateway deployed on its own
- **WHEN** the deployment script is run for the hosting environment with the `gateway` component
- **THEN** the gateway function app is provisioned if absent, configured, and the application is published

#### Scenario: Gateway included in a full deployment
- **WHEN** the deployment script is run with all components against the hosting environment
- **THEN** the gateway is deployed in addition to the existing components

#### Scenario: Gateway skipped in a full deployment elsewhere
- **WHEN** the deployment script is run with all components against an environment that does not host the gateway
- **THEN** the gateway is not deployed and the remaining components succeed

#### Scenario: Existing components unaffected
- **WHEN** an existing component is deployed on its own
- **THEN** the gateway function app is neither created nor modified

## ADDED Requirements

### Requirement: One path provisions Service Bus queues
Queue provisioning SHALL have a single path that derives every queue name from the environment prefix.
Templates that enumerate queue names for specific environments SHALL NOT be maintained alongside it.

#### Scenario: Queue names derived from the environment
- **WHEN** queues are provisioned for an environment
- **THEN** every queue name is derived from that environment's prefix rather than read from a list written per environment

#### Scenario: Adding an environment needs no template edit
- **WHEN** a new environment is added
- **THEN** its queues are created by the deployment script without editing an infrastructure template
