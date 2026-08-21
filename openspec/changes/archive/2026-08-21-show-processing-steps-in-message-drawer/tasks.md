## 1. Drawer Processing-Step Presentation

- [x] 1.1 Review `MessageLogDrawer` data states and ensure the selected message's processing-step records are rendered in the drawer through the shared detail component.
- [x] 1.2 Update `ProcessingStepsCard` as needed so sequence, name, complete multiline detail, and timestamp remain readable in the drawer without truncation.
- [x] 1.3 Ensure error steps retain visual emphasis and add a clear empty state when no processing steps exist.
- [x] 1.4 Ensure loading or log-detail retrieval failures are represented locally without closing or breaking the drawer.

## 2. Behavior Preservation

- [x] 2.1 Verify closing the drawer leaves the underlying message list, active filters, and search text unchanged.
- [x] 2.2 Extract and reuse the payload card so the drawer and full-detail page both support copy, edit, save, and cancel behavior.
- [x] 2.3 Extract and reuse the reprocess action so the drawer and full-detail page share confirmation, pending, success, and failure behavior.
- [x] 2.4 Add message details, payload, and reprocess controls to the drawer while retaining its full-detail link.
- [x] 2.5 Verify the full-detail link and direct message-detail route continue to support payload editing and reprocessing.

## 3. Validation

- [x] 3.1 Run the dashboard frontend lint and production build, resolving any issues introduced by the change.
- [x] 3.2 Manually verify messages with multiple, multiline, error, and zero processing steps in the drawer at narrow and wide viewport sizes.
