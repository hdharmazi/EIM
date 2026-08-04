## Context

[MessageListPage.tsx](../../../ARM.Integration.Dashboard.Frontend/src/features/messagequeue/MessageListPage.tsx) keeps the status filter in the URL (`?status=`) but keeps search text only in local component `useState` (`search`/`activeSearch`), never synced to the URL. Clicking "View" calls `navigate(`/messages/${m.id}`)`, unmounting the list page entirely; navigating back remounts it fresh, so the search box resets even though the status dropdown (URL-backed) survives. There is no drawer/panel primitive in [components/ui](../../../ARM.Integration.Dashboard.Frontend/src/components/ui) today — only a centered modal [dialog.tsx](../../../ARM.Integration.Dashboard.Frontend/src/components/ui/dialog.tsx) built on `@radix-ui/react-dialog`.

## Goals / Non-Goals

**Goals:**
- Add a reusable slide-in `Sheet` component (Radix Dialog primitive, side-anchored) following the same conventions as `dialog.tsx`.
- Add a `MessageLogDrawer` that reuses the existing detail data hooks (`useMessage`, `useLogDetails`) to show details/log/processing-steps in that drawer.
- Wire the list's "View" button to open the drawer (selected message id in component state) instead of navigating away.
- Move `search` into the URL search params (mirroring how `status` already works) so all filter state is derived from the URL and therefore survives remounts and back/forward navigation.

**Non-Goals:**
- Removing or restyling the existing `/messages/:id` full detail page — it stays as-is for deep links, refresh, and actions (edit payload, reprocess) not covered by the lightweight drawer.
- Changing the API surface — the drawer reuses existing message/log-details endpoints.
- General app-wide URL-state framework — this only fixes the Message Queue list.

## Decisions

- **Build a `Sheet` component on `@radix-ui/react-dialog` rather than adding a new dependency.** The dependency is already installed and used by `dialog.tsx`; a side-anchored variant only needs different content positioning/animation classes (`inset-y-0 right-0 h-full w-full max-w-md` instead of centered), keeping the component set consistent and dependency-free.
- **Drawer shows details/log/processing-steps only; payload editing and reprocess stay on the full page.** Keeps the drawer lightweight and fast for the common "glance at the log" workflow the user asked for, while avoiding duplicating stateful edit/reprocess logic in two places. Users who need to edit or reprocess still click through to `/messages/:id` from the drawer (a small "Open full details" link).
- **Selected-row-for-drawer is local component state (`selectedId: number | null`), not URL state.** The drawer is a transient UI affordance; putting it in the URL would also work but isn't needed since the drawer never needs to be deep-linked or survive a hard refresh — keeping it local avoids extra URL complexity.
- **Move `search` into `useSearchParams` alongside `status`, with the same commit-on-submit behavior.** Reuses the exact pattern already proven for `status`, so the "Filter" button still explicitly applies the search box's current text (avoiding a search-on-every-keystroke re-fetch), but now both survive remounts because the source of truth is the URL, not component state.

## Risks / Trade-offs

- Two ways to view a message's log (drawer vs. full page) → some divergence risk if the log/details rendering changes in one place and not the other. Mitigated by having both share the same `useMessage`/`useLogDetails` hooks and, where practical, the same presentational sub-components (log/detail cards) extracted from `MessageDetailPage.tsx`.
- Moving `search` to the URL changes the shareable/bookmarkable URL shape (adds `?search=`) → this is an improvement (bookmarkable filtered views) with no breaking impact since `search` is optional and defaults to empty.
- Existing bookmarks/links to `/messages/:id` continue to work unchanged since that route is not removed.
