import { useState } from "react";
import { PageHeader } from "@/components/PageHeader";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { formatDateTime } from "@/lib/format";
import {
  dependentRoutes,
  routeKeyOf,
  routeKeyString,
  useOrphanedBacklog,
  usePauseRoute,
  useResumeRoute,
  useRoutes,
  type RouteLifecycle,
  type RouteRecord,
} from "./api";

function lifecycleVariant(lifecycle: RouteLifecycle) {
  switch (lifecycle) {
    case "Live":
      return "success" as const;
    case "Draining":
      return "warning" as const;
    default:
      return "destructive" as const;
  }
}

function ageSince(value: string | null): string {
  if (!value) return "—";
  const started = new Date(value).getTime();
  if (Number.isNaN(started)) return "—";

  const minutes = Math.max(0, Math.floor((Date.now() - started) / 60_000));
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}

export function RouteControlPage() {
  const { data: routes, isLoading, error } = useRoutes();
  const { data: orphans } = useOrphanedBacklog();
  const pause = usePauseRoute();
  const resume = useResumeRoute();
  const [pending, setPending] = useState<RouteRecord | null>(null);

  const busy = pause.isPending || resume.isPending;
  const dependents = pending && routes ? dependentRoutes(pending, routes) : [];

  function confirm() {
    if (!pending) return;
    const key = routeKeyOf(pending);
    const action = pending.enabled ? pause : resume;
    action.mutate(key, { onSettled: () => setPending(null) });
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title="Route Control"
        description="Pause a route to capture its messages instead of processing them. Resume to drain them oldest-first."
      />

      {error && <p className="text-destructive">Could not load routes: {String(error)}</p>}
      {isLoading && <p className="text-muted-foreground">Loading routes…</p>}

      {orphans && orphans.length > 0 && (
        <div className="rounded border border-destructive/50 bg-destructive/5 p-4">
          <p className="font-medium text-destructive">Messages stranded on deleted routes</p>
          <p className="mt-1 text-sm text-muted-foreground">
            Both sweeps match on the route table, so these will never be picked up. Re-create the
            route to release them.
          </p>
          <ul className="mt-3 space-y-1 text-sm">
            {orphans.map((o) => (
              <li key={`${o.environment}/${o.sourceSystem}/${o.sourceEntity}`}>
                <span className="font-mono">
                  {o.environment}/{o.sourceSystem}/{o.sourceEntity}
                </span>
                {" — "}
                {o.count} message{o.count === 1 ? "" : "s"}, oldest {formatDateTime(o.oldestUtc)}
              </li>
            ))}
          </ul>
        </div>
      )}

      {routes && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Environment</TableHead>
              <TableHead>Source</TableHead>
              <TableHead>Entity</TableHead>
              <TableHead>Orchestration</TableHead>
              <TableHead>State</TableHead>
              <TableHead className="text-right">Held</TableHead>
              <TableHead className="text-right">Retrying</TableHead>
              <TableHead className="text-right">Oldest held</TableHead>
              <TableHead>Changed by</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {routes.map((route) => (
              <TableRow key={routeKeyString(routeKeyOf(route))}>
                <TableCell>{route.environment}</TableCell>
                <TableCell>{route.sourceSystem}</TableCell>
                <TableCell className="font-mono text-xs">{route.sourceEntity}</TableCell>
                <TableCell className="font-mono text-xs">{route.orchestrationType}</TableCell>
                <TableCell>
                  <Badge variant={lifecycleVariant(route.lifecycle)}>{route.lifecycle}</Badge>
                </TableCell>
                <TableCell className="text-right">{route.heldCount || "—"}</TableCell>
                <TableCell className="text-right">{route.retryScheduledCount || "—"}</TableCell>
                <TableCell className="text-right">{ageSince(route.oldestHeldUtc)}</TableCell>
                <TableCell className="text-xs text-muted-foreground">
                  {route.modifiedBy ? `${route.modifiedBy} · ${formatDateTime(route.modifiedDateTime)}` : "—"}
                </TableCell>
                <TableCell className="text-right">
                  <Button
                    variant={route.enabled ? "destructive" : "default"}
                    size="sm"
                    disabled={busy}
                    onClick={() => setPending(route)}
                  >
                    {route.enabled ? "Pause" : "Resume"}
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Dialog open={pending !== null} onOpenChange={(open) => !open && setPending(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{pending?.enabled ? "Pause route" : "Resume route"}</DialogTitle>
            <DialogDescription asChild>
              <div className="space-y-3 text-sm">
                <p className="font-mono">{pending && routeKeyString(routeKeyOf(pending))}</p>

                {pending?.enabled ? (
                  <p>
                    New messages will be captured at <strong>Held</strong> instead of processed. Nothing
                    is lost, and they replay in order when you resume.
                  </p>
                ) : (
                  <>
                    <p>
                      {pending?.heldCount ? (
                        <>
                          <strong>{pending.heldCount}</strong> held message
                          {pending.heldCount === 1 ? "" : "s"} will replay oldest-first.
                        </>
                      ) : (
                        <>Processing resumes immediately.</>
                      )}
                    </p>
                    {(pending?.heldCount ?? 0) > 0 && (
                      <p className="text-muted-foreground">
                        Replay overwrites the destination with what the source sent. Any edit made
                        directly in the destination during the pause will be lost.
                      </p>
                    )}
                  </>
                )}

                {dependents.length > 0 && (
                  <div className="rounded border border-amber-500/40 bg-amber-500/5 p-3">
                    <p className="font-medium">This entity also flows the other way.</p>
                    <ul className="mt-1 list-disc pl-5 font-mono text-xs">
                      {dependents.map((d) => (
                        <li key={routeKeyString(routeKeyOf(d))}>
                          {d.sourceSystem} → {d.sourceEntity} ({d.lifecycle})
                        </li>
                      ))}
                    </ul>
                    <p className="mt-2 text-xs text-muted-foreground">
                      Leaving the opposite direction live means edits keep flowing while this side is
                      paused, and those changes will be overwritten on release.
                    </p>
                  </div>
                )}
              </div>
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setPending(null)} disabled={busy}>
              Cancel
            </Button>
            <Button
              variant={pending?.enabled ? "destructive" : "default"}
              onClick={confirm}
              disabled={busy}
            >
              {pending?.enabled ? "Pause route" : "Resume route"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
