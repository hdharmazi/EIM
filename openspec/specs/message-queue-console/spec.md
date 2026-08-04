# message-queue-console Specification

## Purpose

Define the Message Queue console's list-page behavior: filter/search state persistence and the log-preview drawer used to review a message's log without navigating away from the list.

## Requirements

### Requirement: Log-preview drawer
The system SHALL provide a slide-in drawer on the Message Queue list page that displays a selected message's details, message/error log, and processing steps without navigating away from the list.

#### Scenario: Opening the drawer from the list
- **WHEN** a user clicks "View" on a row in the Message Queue list
- **THEN** a drawer opens over the list showing that message's details, message/error log, and processing steps, and the list remains visible and unchanged underneath

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
