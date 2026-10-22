## ADDED Requirements

### Requirement: Automation host name and layout
The Service Bus and timer host SHALL be the project `Wilson.Integration.Automation`, with root namespace `Wilson.Integration.Automation`. Trigger classes SHALL live in `Functions/` and contain only binding, logging scope and a call to one processor. Processing that delivers into D365 SHALL live in `Inbound/`, and processing of D365 events outward SHALL live in `Outbound/`. No project, file or reference SHALL use the name `ARM.Integration.EIM.Customer`, except archived OpenSpec changes and the active change `fix-address-source-business-key`.

#### Scenario: No stale project name
- **WHEN** the repository, excluding `openspec/changes/archive/**` and `openspec/changes/fix-address-source-business-key/**`, is searched for `ARM.Integration.EIM.Customer`
- **THEN** there are no matches

#### Scenario: Trigger classes are thin
- **WHEN** a Service Bus message arrives on `%D365FscDataEventQueue%`
- **THEN** `Functions/QueueDataEventsHandler` passes it to `Outbound/DataEventProcessor`, which produces the same outcome and message settlement as before the reorganisation

### Requirement: Function names are stable
The Automation host SHALL register exactly these functions, with unchanged names, trigger expressions and Service Bus connection setting:

| Function | Trigger |
|---|---|
| `QueueCaptureCustomer` | `%CaptureToD365Queue%` |
| `QueueDataEventsHandler` | `%D365FscDataEventQueue%` |
| `QueueMessageQueueBusinessEventHandler` | `%D365BusinessEventQueue%` |
| `AwaitingD365SweepHandler` | `%AwaitingD365SweepSchedule%` |
| `ReleaseSweepHandler` | `%ReleaseSweepSchedule%` |
| `RetrySweepHandler` | `%RetrySweepSchedule%` |

All Service Bus triggers SHALL use `Connection = "ServiceBus"`.

#### Scenario: Function inventory pinned
- **WHEN** the Automation assembly is inspected for `[Function]` attributes
- **THEN** the names are exactly the six listed, each with its listed trigger expression

#### Scenario: Renaming a trigger class is caught
- **WHEN** a developer renames `RetrySweepHandler` to `RetrySweepFunction`
- **THEN** `AutomationFunctionNamesTests` fails, naming the missing function and the deployment gated-function list to update

### Requirement: No inactive trigger templates
The Automation host SHALL NOT contain inactive or commented-out trigger classes. `PollSourceHandler` SHALL be deleted. Its `PollSchedule` setting SHALL NOT be a key in any committed local settings sample and SHALL NOT be given a value in any `script/deployment/config/deploy.*.json`; it SHALL appear there only as an entry of `automation.retiredSettings`. `SamplePoll_To_Target`, `EIMWatermark` and `WATERMARKTABLE` SHALL remain.

#### Scenario: PollSourceHandler removed
- **WHEN** the Automation assembly and the repository, excluding `openspec/changes/archive/**`, are inspected
- **THEN** there is no `PollSourceHandler` type and no `PollSourceHandler.cs` file

#### Scenario: Sample has no retired settings
- **WHEN** `Wilson.Integration.Automation/local.settings.sample.json` is read
- **THEN** it contains no `PollSchedule` key and no key starting with `PollSource`

#### Scenario: Sample poll orchestration kept
- **WHEN** the solution builds after `PollSourceHandler` is deleted
- **THEN** `SamplePoll_To_Target` still compiles and its documentation no longer references `PollSourceHandler`

### Requirement: Engine and client tests are separate
`ARM.Integration.EIM.Tests` SHALL reference only `ARM.Integration.EIM` and `ARM.Integration.Core`. Tests that need Wilson orchestrations or the Automation host SHALL live in `Wilson.Integration.Tests`. `EngineBoundaryTests` SHALL live in `Wilson.Integration.Tests` and keep passing.

#### Scenario: Engine test project has no client reference
- **WHEN** `ARM.Integration.EIM.Tests.csproj` is inspected
- **THEN** it has no project reference to `Wilson.Integration.Orchestrations` or `Wilson.Integration.Automation`

#### Scenario: Engine boundary holds
- **WHEN** `dotnet test Wilson.Integration.Tests` runs
- **THEN** `EngineBoundaryTests` passes, proving that the engine does not reference the client and that every registered orchestration is client-owned

#### Scenario: Moved tests keep their fixtures
- **WHEN** `CustomerAddressDeleteToCaptureTests` runs from `Wilson.Integration.Tests`
- **THEN** it reads `014_seed_customer_address_delete_route.sql` and its JSON fixtures from the test output and passes

### Requirement: Fail-fast configuration validation
The Automation host SHALL check at startup, before registering services, that `EIMDB`, `ServiceBus`, the five queue settings and the three sweep schedules are present. The gateway SHALL build its options at startup, requiring `EIMDB` and `Gateway:BaseUrl`. A missing, empty or unresolved Key Vault reference value SHALL stop startup with one error naming every missing setting and never any value.

#### Scenario: Missing Service Bus connection
- **WHEN** the Automation host starts without `ServiceBus`
- **THEN** startup fails with an error naming `ServiceBus`, rather than repeatedly failing to start queue listeners

#### Scenario: Several settings missing
- **WHEN** the Automation host starts without `EIMDB` and `RetrySweepSchedule`
- **THEN** one error names both settings

#### Scenario: Gateway base URL missing
- **WHEN** the gateway starts without `Gateway:BaseUrl`
- **THEN** startup fails naming `Gateway:BaseUrl`, before any request is served

#### Scenario: Secret values not logged
- **WHEN** configuration validation reports an error
- **THEN** the message names the settings and contains no configuration value

### Requirement: Sample local settings and settings switch
`Wilson.Integration.Automation` and `ARM.Integration.D365FSC.Gateway` SHALL each commit a `local.settings.sample.json` with placeholders or local-emulator values for every setting the host reads. Each SHALL support `-p:LocalSettings=<name>`, which copies `local.settings.<name>.json` into the build output as `local.settings.json`. Local settings files other than the samples SHALL be excluded from source control and from publish output.

#### Scenario: Sample covers the required settings
- **WHEN** `RequiredSettings` groups for Automation are compared with `local.settings.sample.json`
- **THEN** every required key is present in the sample

#### Scenario: Named settings file selected
- **WHEN** a developer builds with `-p:LocalSettings=mydev` and `local.settings.mydev.json` exists
- **THEN** the output folder contains it as `local.settings.json`

#### Scenario: Local settings not published
- **WHEN** the Automation host is published
- **THEN** the publish output contains no `local.settings.json`

### Requirement: Secrets are not committed
The repository SHALL NOT contain client secrets, shared access keys, storage account keys or connection-string passwords. A test SHALL scan the repository for these patterns, excluding build output, dependencies and ignored local settings.

#### Scenario: Secret scan
- **WHEN** `SecretScanTests` runs
- **THEN** no client secret, `SharedAccessKey=`, `AccountKey=` or password value other than a `<...>` placeholder is found

### Requirement: Local run ports
Locally, the Automation host SHALL listen on port 7071 and the gateway on port 7072, matching `run.bat` and the README. No container or Docker launch profile SHALL remain.

#### Scenario: Automation local port
- **WHEN** a developer starts `Wilson.Integration.Automation` with its launch profile or `run.bat`
- **THEN** the Functions host listens on `http://localhost:7071`
