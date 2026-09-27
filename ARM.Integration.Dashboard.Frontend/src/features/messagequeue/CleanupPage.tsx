import * as React from "react";
import { AlertTriangle, Loader2 } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useToast } from "@/components/ui/toast";
import { MESSAGE_STATUSES } from "@/lib/status";
import { useStatusCounts, type CleanupParams } from "./api";
import { markCleanupNotified, startCleanup, useCleanupRun } from "./cleanupRun";

const ALL = "all";
const FILTERS_KEY = "cleanup-filters";

// Per tab: survives a reload so the Delete button is not left disabled by a cleared form.
function loadFilters(): { status: string; olderThanDays: string } {
  try {
    const saved = JSON.parse(sessionStorage.getItem(FILTERS_KEY) ?? "null");
    if (saved && typeof saved.status === "string" && typeof saved.olderThanDays === "string") return saved;
  } catch {
    // Ignore a corrupt entry and fall back to defaults.
  }
  return { status: ALL, olderThanDays: "" };
}

export function CleanupPage() {
  const { toast } = useToast();
  const counts = useStatusCounts();
  const run = useCleanupRun();

  const [status, setStatus] = React.useState<string>(() => loadFilters().status);
  const [olderThanDays, setOlderThanDays] = React.useState(() => loadFilters().olderThanDays);
  const [confirmOpen, setConfirmOpen] = React.useState(false);

  React.useEffect(() => {
    sessionStorage.setItem(FILTERS_KEY, JSON.stringify({ status, olderThanDays }));
  }, [status, olderThanDays]);

  const running = run.phase === "running";
  const hasFilter = status !== ALL || olderThanDays.trim() !== "";
  const refusal = run.phase === "refused" && !run.notified ? run.message : null;

  // The run can finish while this page is unmounted, so the outcome is announced on next view.
  React.useEffect(() => {
    if (run.notified) return;

    if (run.phase === "done") {
      const none = run.deleted === 0;
      toast({
        variant: none ? "warning" : "success",
        title: none ? "No records matched" : "Cleanup complete",
        description: none
          ? "No records matched the selected filter(s). Nothing was deleted."
          : `Deleted ${run.deleted} record(s).`,
      });
      markCleanupNotified();
    } else if (run.phase === "failed") {
      toast({
        variant: "destructive",
        title: "Cleanup stopped",
        description: `${run.message} Deleted ${run.deleted} record(s) before stopping; run again to continue.`,
      });
      markCleanupNotified();
    }
  }, [run.phase, run.notified, run.deleted, run.message, toast]);

  function runCleanup(acknowledgeProtected: boolean) {
    setConfirmOpen(false);

    const params: CleanupParams = {
      status: status === ALL ? undefined : Number(status),
      olderThanDays: olderThanDays.trim() ? Number(olderThanDays) : undefined,
      acknowledgeProtected,
    };

    void startCleanup(params);
  }

  function acknowledgeAndRun() {
    const params = run.params;
    markCleanupNotified();
    if (params) void startCleanup({ ...params, acknowledgeProtected: true });
  }

  return (
    <div>
      <PageHeader title="Cleanup Message Queue" description="Permanently delete queue records" />

      <Card className="mb-4">
        <CardHeader className="py-3">
          <CardTitle className="text-sm">Current Record Counts by Status</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          {MESSAGE_STATUSES.map((s) => (
            <Badge key={s.value} variant="secondary" className="text-sm">
              {s.label}: {counts.data?.[String(s.value)] ?? 0}
            </Badge>
          ))}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="py-3">
          <CardTitle className="text-sm">Delete Records</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 md:grid-cols-2">
            <div className="space-y-1">
              <Label>Status Filter</Label>
              <Select value={status} onValueChange={setStatus}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>All Statuses</SelectItem>
                  {MESSAGE_STATUSES.map((s) => (
                    <SelectItem key={s.value} value={String(s.value)}>
                      {s.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground">Leave as "All" to target all statuses.</p>
            </div>
            <div className="space-y-1">
              <Label>Older Than (days)</Label>
              <Input
                type="number"
                min={1}
                placeholder="e.g. 30"
                value={olderThanDays}
                onChange={(e) => setOlderThanDays(e.target.value)}
              />
              <p className="text-xs text-muted-foreground">Leave empty to ignore age.</p>
            </div>
          </div>

          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>
              This permanently deletes matching records and their log details. This cannot be undone.
              At least one filter is required. If both a status and an age are set, only records
              matching both are deleted. With the status left as "All", only Completed and Failed
              records are deleted — held, retrying and in-flight messages are never deleted in bulk.
            </span>
          </div>

          {running && (
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" />
              {run.remaining === null ? (
                <span>Deleting… the first update arrives within about 20 seconds.</span>
              ) : (
                <span>
                  Deleted {run.deleted.toLocaleString()} record(s), {run.remaining.toLocaleString()} remaining…
                  You can leave this page; the cleanup continues while this browser tab stays open.
                </span>
              )}
            </div>
          )}

          {run.phase === "failed" && (
            <p className="text-sm text-destructive">
              Last run stopped after deleting {run.deleted.toLocaleString()} record(s): {run.message}
            </p>
          )}

          <Button variant="destructive" disabled={!hasFilter || running} onClick={() => setConfirmOpen(true)}>
            {running ? "Deleting…" : "Delete Records"}
          </Button>
        </CardContent>
      </Card>

      <Dialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Confirm Cleanup</DialogTitle>
            <DialogDescription>
              Are you sure you want to delete matching records? This action cannot be undone.
              {status !== ALL && olderThanDays.trim() !== "" && (
                <>
                  {" "}This deletes only records with the selected status that are also older than{" "}
                  {olderThanDays} day(s).
                </>
              )}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmOpen(false)}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => runCleanup(false)} disabled={running}>
              Yes, Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Second acknowledgement, quoting the server so the protection reasons live in one place. */}
      <Dialog open={refusal !== null} onOpenChange={(open) => !open && markCleanupNotified()}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>This status is protected</DialogTitle>
            <DialogDescription>
              {refusal} Deleting these records may discard work that has not finished.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={markCleanupNotified}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={acknowledgeAndRun} disabled={running}>
              Delete Anyway
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
