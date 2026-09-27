import * as React from "react";
import { ApiError, apiFetch, queryClient } from "@/lib/api";
import type { CleanupParams, CleanupResult } from "./api";

export type CleanupPhase = "idle" | "running" | "done" | "refused" | "failed";

export interface CleanupRunState {
  phase: CleanupPhase;
  deleted: number;
  /** Null until the first request returns. */
  remaining: number | null;
  message: string | null;
  params: CleanupParams | null;
  /** Whether the page has already shown this outcome, so a remount does not repeat it. */
  notified: boolean;
}

const initial: CleanupRunState = {
  phase: "idle",
  deleted: 0,
  remaining: null,
  message: null,
  params: null,
  notified: true,
};

/**
 * Lives outside the page so a run survives navigation and a second run cannot start while one is
 * in flight. A full page reload still stops the loop; committed batches are kept and a new run
 * resumes from what remains.
 */
let state: CleanupRunState = initial;
const listeners = new Set<() => void>();

function set(next: Partial<CleanupRunState>) {
  state = { ...state, ...next };
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function request(params: CleanupParams) {
  const q = new URLSearchParams();
  if (params.status !== undefined) q.set("status", String(params.status));
  if (params.olderThanDays !== undefined) q.set("olderThanDays", String(params.olderThanDays));
  if (params.acknowledgeProtected) q.set("acknowledgeProtected", "true");
  return apiFetch<CleanupResult>(`/api/messagequeue/cleanup?${q.toString()}`, { method: "POST" });
}

export async function startCleanup(params: CleanupParams): Promise<void> {
  if (state.phase === "running") return;

  set({ phase: "running", deleted: 0, remaining: null, message: null, params, notified: false });

  try {
    // Each request is bounded by a server-side time budget, so a large backlog takes several.
    for (;;) {
      const result = await request(params);
      set({ deleted: state.deleted + result.deleted, remaining: result.remaining });
      console.info("[cleanup] batch", { deleted: result.deleted, total: state.deleted, remaining: result.remaining });
      void queryClient.invalidateQueries({ queryKey: ["messages"] });

      if (result.remaining === 0) break;

      if (result.deleted === 0) {
        // Not a completion: rows are still eligible but this request could not claim any.
        const message = `Stopped early: ${result.remaining} eligible record(s) remain but none could be deleted.`;
        console.warn("[cleanup] stopped", message);
        set({ phase: "failed", message });
        return;
      }
    }
    set({ phase: "done" });
  } catch (err) {
    console.error("[cleanup] request failed", err);
    const body = err instanceof ApiError ? (err.body as FailureBody | undefined) : undefined;
    if (body?.refused) {
      set({ phase: "refused", message: body.message ?? null });
    } else {
      set({ phase: "failed", message: describeFailure(err, body) });
    }
  } finally {
    void queryClient.invalidateQueries({ queryKey: ["messages"] });
  }
}

interface FailureBody {
  refused?: boolean;
  message?: string;
  correlationId?: string;
}

/** The server withholds exception detail; the correlation id finds the matching API log line. */
function describeFailure(err: unknown, body: FailureBody | undefined): string {
  if (err instanceof ApiError) {
    const parts = [`HTTP ${err.status}.`];
    if (body?.message) parts.push(body.message);
    if (body?.correlationId) parts.push(`Correlation id: ${body.correlationId}.`);
    return parts.join(" ");
  }
  // Network failures (API stopped, proxy dropped the connection) arrive as a plain TypeError.
  return err instanceof Error ? `${err.name}: ${err.message}` : String(err);
}

export function markCleanupNotified() {
  set({ notified: true });
}

export function useCleanupRun(): CleanupRunState {
  return React.useSyncExternalStore(subscribe, () => state);
}
