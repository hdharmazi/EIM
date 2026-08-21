## Context

The Message Queue list opens `MessageLogDrawer` for a selected message while preserving the list context. The full-detail page currently owns payload editing and reprocessing UI, while the drawer only shows the message log and processing steps. The drawer must offer the same detail and action surface as the full-detail page without duplicating behavior that could drift.

## Goals / Non-Goals

**Goals:**
- Present message metadata, message/error log, editable payload, processing steps, and reprocessing in the drawer.
- Share payload and reprocess behavior between the drawer and full-detail page.
- Present processing steps in the message drawer with the same step content available on the full-detail page.
- Keep long and multiline details readable within the narrower drawer layout.
- Retain sequence, timestamp, and error emphasis for every step.
- Handle loading, empty, and error states without disrupting the surrounding message list.

**Non-Goals:**
- Add or modify API endpoints, database tables, or orchestration logging.
- Remove or change the full message-detail route.
- Change message-list filter or search persistence.

## Decisions

- Reuse `ProcessingStepsCard` in the drawer instead of creating a drawer-specific renderer. This keeps the drawer and full-detail page behavior aligned and prevents presentation drift. A duplicated component was rejected because future changes could produce inconsistent details.
- Extract the full page's payload UI into a shared `MessagePayloadCard`, including copy, edit, save, and cancel behavior, and render it from both surfaces.
- Extract reprocessing into a shared action component so confirmation, pending state, and toast results behave identically in the drawer and full-detail page.
- Continue using the existing `useLogDetails` request. Processing-step records already come from `/api/messagequeue/{id}/log-details`, so an aggregate endpoint would add API work without improving the user-visible result.
- Adapt the shared card's layout responsively where necessary so names, complete multiline details, and timestamps remain readable in the drawer. Truncating details was rejected because it conflicts with diagnostic use.
- Keep the full-detail navigation link for direct/deep-linked access even though the drawer offers equivalent message actions.
- Verify the behavior with the existing frontend build/lint workflow and focused manual scenarios. Introducing a new frontend test framework is outside this change.

## Risks / Trade-offs

- [Very long processing details increase drawer height] → Preserve scrolling within the existing sheet and wrap preformatted text rather than clipping it.
- [Shared responsive changes affect the full-detail page] → Use responsive layout classes that preserve the current wide-screen presentation while improving narrow widths.
- [The current source already includes the processing card] → Treat implementation as a conformance and UX-hardening change; verify all required fields and states before making only necessary edits.
- [Log-detail retrieval fails independently of message retrieval] → Keep the drawer open, show a localized error state, and retain access to the full-detail link.
- [Mutations in one view can diverge from cached data in the other] → Reuse existing React Query mutations and cache invalidation through shared components.
