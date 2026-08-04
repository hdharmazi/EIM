## Why

Analysts triaging the Message Queue console lose their working context in two ways: (1) viewing a message's log requires a full-page navigation away from the list, making it slow to skim several messages in a row, and (2) any search text and status filter applied to the list is discarded when they navigate back from a message's detail view, forcing them to re-enter the same filter repeatedly during a triage session.

## What Changes

- Add a slide-in drawer (side panel) to the Message Queue list page that shows the message log/details for a selected row without leaving the list.
- Clicking "View" opens the drawer over the current list instead of navigating to `/messages/:id`; the list stays mounted underneath so nothing needs to reload.
- Persist the active search text and status filter across this interaction (and across browser back navigation) so returning to the list shows the same filtered/searched results, instead of resetting to the unfiltered list.
- The existing full detail route (`/messages/:id`, with payload editing and reprocess) remains available (e.g., deep links, refresh) but is no longer the primary path from the list's "View" action.

## Capabilities

### New Capabilities
- `message-queue-console`: list-page behavior for the Message Queue view — filter/search state persistence and the log-preview drawer.

### Modified Capabilities
<!-- None: no existing spec covers this page's behavior today -->

## Impact

- Code: `ARM.Integration.Dashboard.Frontend/src/features/messagequeue/MessageListPage.tsx` (drawer trigger, filter state), new `MessageLogDrawer` component, new `components/ui/sheet.tsx` (Radix Dialog-based slide-in panel, following the existing `dialog.tsx` pattern).
- Filter state moves from local-only `useState` to URL search params (`?status=&search=`) so it survives remounts and back/forward navigation.
- No API or backend changes; reuses existing `/api/messagequeue/:id` and `/api/messagequeue/:id/log-details` endpoints.
- No new dependencies (`@radix-ui/react-dialog` is already used by the existing dialog component).
