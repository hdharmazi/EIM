## 1. Backend: fix filter combination

- [x] 1.1 Search `ARM.Integration.EIM.Tests` (and any Dashboard.API test project) for existing `CleanupAsync` coverage that asserts AND semantics; note tests to update.
- [x] 1.2 In `ARM.Integration.Dashboard.API/Services/MessageQueueRepository.cs`, change `CleanupAsync` to join the status and age conditions with `OR` when both are supplied, while leaving single-filter calls unchanged.
- [x] 1.3 Verify the `MESSAGELOGDETAIL` delete (child records) and the `MESSAGEQUEUETABLE` delete use the same combined WHERE clause and parameters so both statements delete the same row set.
- [x] 1.4 Add/update unit or integration tests covering: status-only delete, age-only delete, combined status+age delete (union, not intersection), and the existing "at least one filter required" validation. (Skipped: no Dashboard.API test project or DB-testing infra exists for this repository layer; covered instead by manual verification in 3.3.)

## 2. Frontend: messaging and copy

- [x] 2.1 In `ARM.Integration.Dashboard.Frontend/src/features/messagequeue/CleanupPage.tsx`, update the filter helper text to state that selecting both a status and an age filter deletes records matching either condition.
- [x] 2.2 Render a distinct toast (e.g., warning/info variant) when `result.deleted === 0`, separate from the generic success toast, so a no-op cleanup is clearly distinguishable.
- [x] 2.3 Update the confirmation dialog copy if needed to reflect the OR semantics before the user confirms a combined-filter delete.

## 3. Verification

- [x] 3.1 Run backend tests for `MessageQueueRepository`/`MessageQueueController`. (Skipped: no automated tests exist for this layer, per 1.4.)
- [x] 3.2 Run `npx tsc --noEmit -p tsconfig.json` and `npm run build` in `ARM.Integration.Dashboard.Frontend`.
- [ ] 3.3 Manually verify against a dev/test database: single-filter cleanup, combined-filter cleanup (confirm union behavior), and the zero-match toast.
