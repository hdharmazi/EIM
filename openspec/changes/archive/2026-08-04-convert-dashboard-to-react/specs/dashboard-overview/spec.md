## ADDED Requirements

### Requirement: Unified dashboard overview screen
The system SHALL provide a single Dashboard landing screen that aggregates operational status into cards, replacing the separate per-status navigation and status pages. The screen SHALL display message counts by status, recent failures, service-bus/simulation configuration health, and environment/user counts.

#### Scenario: Message status counts displayed
- **WHEN** a validated user opens the Dashboard
- **THEN** the screen shows message counts grouped by status (NEW, PROCESSING, COMPLETED, FAILED, DEAD, AWAITING D365) sourced from `api/messagequeue/status-counts`

#### Scenario: Recent failures surfaced
- **WHEN** the Dashboard loads
- **THEN** it shows a list of recent failed messages sourced from `api/messagequeue/recent-failed`

#### Scenario: Service bus health indicator
- **WHEN** the Dashboard loads
- **THEN** it shows whether Service Bus is configured based on `api/simulation/config`

#### Scenario: Drill-down to filtered message list
- **WHEN** a user clicks a status card (e.g. FAILED)
- **THEN** the SPA navigates to the Message Queue list pre-filtered to that status via a query parameter, without a dedicated per-status page

### Requirement: Live-refreshing status data
Dashboard status cards SHALL reflect current data and SHALL support refresh (manual and/or automatic) without a full page reload.

#### Scenario: Refresh updates counts
- **WHEN** the underlying data changes and the user refreshes the Dashboard
- **THEN** the status cards update to the latest counts without reloading the page
