## ADDED Requirements

### Requirement: Gateway deployment component
The deployment script SHALL support a `gateway` component that provisions and deploys the D365 F&SC
pass-through gateway function app, and SHALL include it when all components are deployed.

#### Scenario: Gateway deployed on its own
- **WHEN** the deployment script is run for an environment with the `gateway` component
- **THEN** the gateway function app is provisioned if absent, configured, and the application is published

#### Scenario: Gateway included in a full deployment
- **WHEN** the deployment script is run with all components
- **THEN** the gateway is deployed in addition to the existing components

#### Scenario: Existing components unaffected
- **WHEN** an existing component is deployed on its own
- **THEN** the gateway function app is neither created nor modified

### Requirement: Gateway hosting profile
The gateway function app SHALL be provisioned on a Linux Flex Consumption plan with an always-ready
instance count and a maximum instance count, both configurable per environment.

#### Scenario: Scale bounds applied
- **WHEN** the gateway is provisioned
- **THEN** the configured always-ready and maximum instance counts are applied to the app

#### Scenario: Plan-based creation not reused
- **WHEN** the gateway is provisioned
- **THEN** it is created using the Flex Consumption provisioning path rather than the app-service-plan path used by the existing function app

### Requirement: Gateway application settings
Deployment SHALL apply the gateway's required application settings, including the EIM database
connection, the gateway base URL used for response link rewriting, the request payload size limit
and the idempotency retention period.

#### Scenario: Settings applied on deployment
- **WHEN** the gateway component is deployed
- **THEN** all required application settings are present on the function app

#### Scenario: Missing required setting fails fast
- **WHEN** a required gateway setting is absent from the environment's deployment configuration
- **THEN** the deployment fails with a message naming the missing setting rather than deploying an unusable app
