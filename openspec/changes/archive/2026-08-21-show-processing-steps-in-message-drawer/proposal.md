## Why

Users need to inspect and act on a message without leaving the message-list context. The drawer currently presents only part of the full-detail experience, so it must expose the same message details, log, payload controls, processing history, and reprocess action as the full-detail page.

## What Changes

- Require the message-view drawer to display message details, message/error log, editable payload, and processing steps.
- Show every processing step with its sequence, name, complete multiline detail, and timestamp.
- Preserve error emphasis and provide a clear empty state when no processing steps exist.
- Add payload copy/edit/save/cancel controls and a reprocess action to the drawer.
- Keep the existing full-detail link and message-list filter/search state unchanged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `message-queue-console`: Require the message-view drawer to provide the same details, payload actions, processing history, and reprocess capability as the full-detail view.

## Impact

- Frontend message drawer, full-detail page, and shared message-detail/action components.
- Existing message queue console specification and UI verification coverage.
- No API, database, message-processing, or dependency changes are expected because the drawer already retrieves log-detail records.
