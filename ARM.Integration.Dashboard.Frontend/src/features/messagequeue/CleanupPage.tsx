import * as React from "react";
import { AlertTriangle } from "lucide-react";
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
import { ApiError } from "@/lib/api";
import { MESSAGE_STATUSES } from "@/lib/status";
import { useCleanup, useStatusCounts, type CleanupParams, type CleanupResult } from "./api";

const ALL = "all";

export function CleanupPage() {
  const { toast } = useToast();
  const counts = useStatusCounts();
  const cleanup = useCleanup();

  const [status, setStatus] = React.useState<string>(ALL);
  const [olderThanDays, setOlderThanDays] = React.useState("");
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [refusal, setRefusal] = React.useState<string | null>(null);
  const [progress, setProgress] = React.useState<{ deleted: number; remaining: number } | null>(null);

  const hasFilter = status !== ALL || olderThanDays.trim() !== "";

  async function runCleanup(acknowledgeProtected: boolean) {
    setConfirmOpen(false);
    setRefusal(null);
    setProgress(null);

    const params: CleanupParams = {
      status: status === ALL ? undefined : Number(status),
      olderThanDays: olderThanDays.trim() ? Number(olderThanDays) : undefined,
      acknowledgeProtected,
    };

    let total = 0;

    try {
      // Each request is bounded by a server-side time budget, so a large backlog takes several.
      for (;;) {
        const result = await cleanup.mutateAsync(params);
        total += result.deleted;
        setProgress({ deleted: total, remaining: result.remaining });

        // deleted === 0 also breaks the loop if rows remain but none could be claimed.
        if (result.remaining === 0 || result.deleted === 0) break;
      }

      toast({
        variant: total === 0 ? "warning" : "success",
        title: total === 0 ? "No records matched" : "Cleanup complete",
        description:
          total === 0
            ? "No records matched the selected filter(s). Nothing was deleted."
            : `Deleted ${total} record(s).`,
      });
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as CleanupResult | undefined) : undefined;
      if (body?.refused) {
        // Surfaced as a second confirmation rather than an error: the operator may still proceed.
        setRefusal(body.message);
        return;
      }
      toast({ variant: "destructive", title: "Cleanup failed", description: String(err) });
    } finally {
      counts.refetch();
    }
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

          {progress && (
            <p className="text-sm text-muted-foreground">
              Deleted {progress.deleted} record(s){progress.remaining > 0 ? `, ${progress.remaining} remaining…` : "."}
            </p>
          )}

          <Button variant="destructive" disabled={!hasFilter || cleanup.isPending} onClick={() => setConfirmOpen(true)}>
            {cleanup.isPending ? "Deleting…" : "Delete Records"}
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
            <Button variant="destructive" onClick={() => runCleanup(false)} disabled={cleanup.isPending}>
              Yes, Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Second acknowledgement, quoting the server so the protection reasons live in one place. */}
      <Dialog open={refusal !== null} onOpenChange={(open) => !open && setRefusal(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>This status is protected</DialogTitle>
            <DialogDescription>
              {refusal} Deleting these records may discard work that has not finished.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRefusal(null)}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => runCleanup(true)} disabled={cleanup.isPending}>
              Delete Anyway
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
