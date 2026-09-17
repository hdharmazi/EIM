## Why

The Cleanup Queue screen's "Delete Records" action reports success ("Deleted 0
record(s).") but removes nothing whenever both a status and an "older than
days" filter are set. `MessageQueueRepository.CleanupAsync` combines the two
filters with `AND`, so a status that has no records past the age threshold
(or vice versa) silently matches zero rows. The UI never explains that the
filters narrow each other, so a user selecting both filters to broadly purge
old history reasonably expects an `OR`-style purge and instead gets a
no-op with no indication why.

## What Changes

- Change `CleanupAsync`'s filter combination from `AND` to `OR` when both a
  status and an age filter are supplied, so cleanup behaves as a broad purge
  ("delete this status, or anything older than N days") instead of a narrow
  intersection that most callers cannot predict.
- Update the Cleanup Queue page copy to state how the two filters combine
  (matches either condition when both are set) so behavior is predictable
  without reading the API.
- Surface a clearer "no records matched" state in the result toast when
  `deleted == 0`, distinct from a generic success message, so a no-op reads as
  informative rather than ambiguous.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `message-queue-management`: The "Cleanup utility" requirement's filter
  combination behavior changes from AND to OR when both a status and an
  older-than-days filter are provided, and the zero-match result becomes an
  explicit, distinguishable outcome.

## Impact

- `ARM.Integration.Dashboard.API/Services/MessageQueueRepository.cs` —
  `CleanupAsync` WHERE-clause construction.
- `ARM.Integration.Dashboard.API/Controllers/MessageQueueController.cs` —
  no route/contract change, response shape unchanged.
- `ARM.Integration.Dashboard.Frontend/src/features/messagequeue/CleanupPage.tsx` —
  filter-combination copy and zero-match toast messaging.
- No schema or migration changes; `MESSAGEQUEUETABLE` / `MESSAGELOGDETAIL`
  deletes are unaffected in shape, only in which rows match.
