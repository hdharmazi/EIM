# message-queue-console Specification

## Purpose

Define the Message Queue console's list-page behavior: filter/search state persistence and the log-preview drawer used to review a message's log without navigating away from the list.

## Requirements

### Requirement: Log-preview drawer
The system SHALL provide a slide-in drawer on the Message Queue list page that provides the same message details, message/error log, payload controls, complete processing-step history, and reprocess action as the full Message Detail page without navigating away from the list. Each processing step SHALL display its sequence, name, complete detail, and timestamp; error steps SHALL remain visually distinguishable.

#### Scenario: Opening the drawer from the list
- **WHEN** a user clicks "View" on a row in the Message Queue list
- **THEN** a drawer opens over the list showing message details, message/error log, payload, processing steps, and reprocess action, and the list remains visible and unchanged underneath

#### Scenario: Editing payload in the drawer
- **WHEN** a user copies or edits a message payload in the drawer
- **THEN** the drawer provides the same copy, edit, save, cancel, pending, success, and failure behavior as the full Message Detail page

#### Scenario: Reprocessing from the drawer
- **WHEN** a user selects Reprocess in the drawer and confirms the action
- **THEN** the system reprocesses the selected message and reports the result using the same behavior as the full Message Detail page

#### Scenario: Viewing complete processing-step details
- **WHEN** the selected message has processing steps, including long or multiline detail
- **THEN** the drawer shows every step in sequence with its name, complete readable detail, and timestamp without truncating the detail

#### Scenario: Viewing an error processing step
- **WHEN** a processing step represents an error or its detail starts with a failure indication
- **THEN** the drawer visually distinguishes that step as an error while retaining its complete detail and timestamp

#### Scenario: Message has no processing steps
- **WHEN** the selected message has no processing-step records
- **THEN** the drawer shows a clear empty state instead of an empty or broken processing-step list

#### Scenario: Closing the drawer
- **WHEN** a user closes the drawer (close control, overlay click, or Escape key)
- **THEN** the drawer closes and the underlying list is shown exactly as it was, with no page reload

#### Scenario: Full detail route remains available
- **WHEN** a user navigates directly to a message's detail URL (e.g., a deep link or page refresh)
- **THEN** the full Message Detail page (including payload editing and reprocess) is still shown

### Requirement: Filter and search state persistence
The system SHALL persist the Message Queue list's active status filter and search text in the URL so that it survives opening/closing the log-preview drawer, navigating to a message's detail page and back, and browser back/forward navigation.

#### Scenario: State survives drawer open/close
- **WHEN** a user applies a status filter and search text, then opens and closes the log-preview drawer for a row
- **THEN** the list still shows results narrowed by that same status filter and search text

#### Scenario: State survives back navigation from detail page
- **WHEN** a user applies a status filter and search text, navigates to a message's full detail page, then navigates back
- **THEN** the list is restored with the same status filter, search text, and matching results — not the unfiltered list

#### Scenario: Clearing filters resets state
- **WHEN** a user clicks "Clear" on the list's filter form
- **THEN** the status filter and search text are removed from both the visible form and the URL, and the list shows all records
