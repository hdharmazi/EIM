## ADDED Requirements

### Requirement: Simulation can be switched off and is off unless enabled
The dashboard API SHALL enable simulation only when the configuration value `Simulation:Enabled` parses as boolean `true`. An absent, empty, `false` or unparseable value SHALL mean off. When simulation is off:
- `SimulationController` SHALL NOT be exposed, so every `/api/simulation/*` route (`config`, `run`, `salesorder/parse`) SHALL answer 404 to an authenticated, validated user.
- `SimulationService` and `SalesOrderExcelParser` SHALL NOT be registered.
- `SimulationService`, if constructed, SHALL refuse to run: it SHALL yield one error event stating that simulation is disabled, and SHALL NOT create a Service Bus client or send a message, even when `Simulation:ServiceBusConnectionString` is set.
- The SPA SHALL NOT show the Simulation navigation entry, and the `/simulation` route SHALL show a notice that simulation is not available in this environment instead of the console.

`appsettings.json` SHALL set `Simulation:Enabled` to `false`. `appsettings.Development.json` SHALL set it to `true` for local development.

#### Scenario: Setting absent
- **WHEN** the dashboard API starts with no `Simulation:Enabled` value
- **THEN** `POST /api/simulation/run` from a validated user returns 404 and no Service Bus message is sent

#### Scenario: Setting false
- **WHEN** `Simulation:Enabled` is `false`
- **THEN** the controller feature built for the API does not contain `SimulationController`

#### Scenario: Unparseable value
- **WHEN** `Simulation:Enabled` is `yes`
- **THEN** simulation is off

#### Scenario: Setting true
- **WHEN** `Simulation:Enabled` is `true`
- **THEN** the controller feature contains `SimulationController`, and `GET /api/simulation/config` returns the configured queue names as before

#### Scenario: Service guard with a connection string present
- **WHEN** `SimulationService.RunAsync` is called with `Simulation:Enabled` absent and `Simulation:ServiceBusConnectionString` set
- **THEN** it yields exactly one error event stating that simulation is disabled, and creates no Service Bus client

#### Scenario: Deep link with simulation off
- **WHEN** a validated user opens `/simulation` while `/api/me` reports `features.simulation` as `false`
- **THEN** the SPA shows the "not available in this environment" notice and makes no `/api/simulation/*` request

#### Scenario: Production dashboard
- **WHEN** the production dashboard API is deployed with `Simulation__Enabled=false`
- **THEN** no user, Admin or otherwise, can publish a simulated message through the dashboard
