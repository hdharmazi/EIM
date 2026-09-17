# deployment-automation Specification

## Purpose
TBD - created by archiving change consolidate-deployment-scripts. Update Purpose after archive.
## Requirements
### Requirement: A single entry point deploys the system
The system SHALL provide one deployment script that deploys every component to any supported non-production environment. Deploying SHALL NOT require selecting between alternative scripts or knowing which of several scripts is current.

#### Scenario: Full deployment to an environment
- **WHEN** an operator runs the deployment script for an environment requesting all components
- **THEN** every component is deployed to that environment in the correct order

#### Scenario: Single component deployment
- **WHEN** an operator requests a single component
- **THEN** only that component is deployed and the others are left untouched

#### Scenario: Unsupported environment is rejected
- **WHEN** an operator requests an environment that is not configured
- **THEN** the script stops with a message naming the unconfigured environment, and no action is taken

### Requirement: Resource names derive from the environment
All Azure resource names SHALL be derived from the requested environment through a single definition. Adding an environment SHALL NOT require duplicating or editing deployment logic.

#### Scenario: Names derived consistently
- **WHEN** the script targets an environment
- **THEN** the Function App, storage account, plan, and queue names it uses all correspond to that environment

#### Scenario: Non-conforming resource name
- **WHEN** an existing resource does not follow the naming pattern
- **THEN** its name is supplied as an explicit configuration override rather than requiring a change to deployment logic

#### Scenario: Adding an environment
- **WHEN** a new environment is introduced
- **THEN** it is added by providing configuration, without creating a new script

### Requirement: No credentials appear in committed files
The deployment script and any committed configuration SHALL NOT contain connection strings, keys, passwords, or other credentials. Credentials SHALL be supplied from a source excluded from version control.

#### Scenario: Committed files are free of credentials
- **WHEN** the deployment script and its committed example configuration are inspected
- **THEN** no credential value is present

#### Scenario: Missing configuration is reported clearly
- **WHEN** the configuration for the requested environment is absent
- **THEN** the script stops and identifies what is missing, without falling back to an embedded or default value

#### Scenario: Values may be supplied without a file
- **WHEN** the script runs in an automated context where a configuration file is not available
- **THEN** the required values can be supplied by environment variables instead

### Requirement: Deployment is idempotent
Running the deployment script repeatedly against an environment SHALL be safe. Existing resources SHALL NOT be recreated, and unchanged configuration SHALL NOT cause disruption.

#### Scenario: Second run creates nothing
- **WHEN** the script is run twice in succession against the same environment
- **THEN** the second run creates no resource that the first created

#### Scenario: Existing resources are preserved
- **WHEN** the script runs against an environment whose resources already exist
- **THEN** those resources are updated in place rather than recreated

### Requirement: Intended actions can be previewed
The system SHALL provide a mode that reports the actions the deployment would take without performing them.

#### Scenario: Preview performs no change
- **WHEN** the script is run in preview mode
- **THEN** it reports the resources it would create or update, and makes no change

#### Scenario: Preview reports derived names
- **WHEN** the script is run in preview mode
- **THEN** the resource names it would target are reported, so they can be checked before any action

### Requirement: Components deploy in a defined order
When multiple components are deployed together, the system SHALL apply them in an order that satisfies their dependencies: message queues, then database schema, then the API, then the Functions host, then the frontend.

#### Scenario: Queues precede the Functions host
- **WHEN** all components are deployed to a new environment
- **THEN** the message queues exist before the Functions host is deployed

#### Scenario: Schema precedes application code
- **WHEN** all components are deployed
- **THEN** database migrations are applied before application components are deployed

#### Scenario: API precedes the frontend
- **WHEN** all components are deployed
- **THEN** the API exists before the frontend is deployed and linked to it

### Requirement: A failed step halts the deployment
The system SHALL stop on the first failing step, report which step failed, and exit with a non-zero status. It SHALL NOT continue to subsequent components after a failure.

#### Scenario: Failure stops subsequent components
- **WHEN** a component fails to deploy during a multi-component run
- **THEN** no subsequent component is attempted

#### Scenario: Failure is reported for automation
- **WHEN** a deployment step fails
- **THEN** the script exits with a non-zero status and names the failed step

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

### Requirement: Environment-independent locations are configurable
The resource group and Service Bus namespace SHALL be supplied by configuration rather than assumed constant, so that an environment residing in a different location can be added without altering deployment logic.

#### Scenario: Location comes from configuration
- **WHEN** the script targets an environment
- **THEN** the resource group and Service Bus namespace it uses come from that environment's configuration

#### Scenario: An environment in a different location can be added
- **WHEN** an environment residing in a different resource group and Service Bus namespace is configured
- **THEN** it is deployable without changing the naming or deployment logic

### Requirement: Entra configuration is applied consistently
The system SHALL apply one Entra tenant, client, and API scope per environment to both the frontend build and the API configuration, so the two cannot diverge.

#### Scenario: Frontend and API agree
- **WHEN** an environment's Entra configuration changes and both components are deployed
- **THEN** the frontend build and the API configuration reflect the same tenant, client, and scope

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

### Requirement: One path provisions Service Bus queues
Queue provisioning SHALL have a single path that derives every queue name from the environment prefix.
Templates that enumerate queue names for specific environments SHALL NOT be maintained alongside it.

#### Scenario: Queue names derived from the environment
- **WHEN** queues are provisioned for an environment
- **THEN** every queue name is derived from that environment's prefix rather than read from a list written per environment

#### Scenario: Adding an environment needs no template edit
- **WHEN** a new environment is added
- **THEN** its queues are created by the deployment script without editing an infrastructure template

