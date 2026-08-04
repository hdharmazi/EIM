## 1. Sheet primitive

- [x] 1.1 Create `ARM.Integration.Dashboard.Frontend/src/components/ui/sheet.tsx`: a side-anchored slide-in panel built on `@radix-ui/react-dialog`, following `dialog.tsx`'s structure (`Sheet`, `SheetTrigger`, `SheetPortal`, `SheetOverlay`, `SheetContent` anchored right with slide-in/out animation, `SheetHeader`, `SheetTitle`, `SheetDescription`, `SheetClose`)

## 2. Message log drawer

- [x] 2.1 Extract the message-details / message-log / processing-steps presentational sections from `MessageDetailPage.tsx` into shared sub-components (or a shared render function) usable by both the full page and the new drawer
- [x] 2.2 Create `MessageLogDrawer` component in `features/messagequeue/` that takes a message id, uses `useMessage`/`useLogDetails`, and renders those shared sections inside `SheetContent`
- [x] 2.3 Add an "Open full details" link/button in the drawer that navigates to `/messages/:id`

## 3. Wire up the list page

- [x] 3.1 In `MessageListPage.tsx`, add `selectedId: number | null` state; change the row "View" button to set `selectedId` (open the drawer) instead of calling `navigate`
- [x] 3.2 Render `MessageLogDrawer` in `MessageListPage.tsx`, open when `selectedId` is set, closing clears `selectedId`

## 4. Filter/search URL persistence

- [x] 4.1 In `MessageListPage.tsx`, replace the local `search`/`activeSearch` `useState` with values derived from `searchParams.get("search")`, mirroring the existing `status` pattern
- [x] 4.2 On `applySearch` (Filter button), write `search` into `searchParams` instead of local state
- [x] 4.3 Update `clearFilters` to remove both `status` and `search` from `searchParams`

## 5. Verify

- [x] 5.1 Build `ARM.Integration.Dashboard.Frontend` and confirm no type errors
- [x] 5.2 Manually verify: apply a status + search filter, click "View" on a row, confirm the drawer opens over the list with the log visible and the list/filters unchanged underneath
- [x] 5.3 Manually verify: apply a status + search filter, navigate to `/messages/:id` (e.g. via "Open full details"), then browser-back, and confirm the same filter/search and results are restored
- [x] 5.4 Manually verify: navigating directly to `/messages/:id` still renders the full detail page with payload editing and reprocess intact

## 6. Drawer refinements (post-review)

- [x] 6.1 Widen the drawer (`max-w-2xl`/`3xl` instead of `max-w-md`)
- [x] 6.2 Drawer shows the Message/Error Log and Processing Steps cards (Message Details card removed from the drawer; still shown on the full detail page)
- [x] 6.3 Move "Open full details" to the top of the drawer, under the title/status badge

## 7. Bug fix: full-page "Back" button dropped filters

- [x] 7.1 `MessageLogDrawer`'s "Open full details" now passes the current list URL (path + search params) via `navigate(..., { state: { from } })`
- [x] 7.2 `MessageDetailPage`'s "Back" button now navigates to `location.state.from` (falling back to `/messages` for direct links) instead of hardcoding `/messages`
- [x] 7.3 Manually verified: filter → View → Open full details → click in-page "Back" (not browser back) → same status/search filter and 21 records restored
