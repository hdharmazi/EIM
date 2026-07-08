import * as React from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Eye, EyeOff, Pencil, Trash2 } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useToast } from "@/components/ui/toast";
import { useDeleteEnvironment, useEnvironments, type EnvironmentRecord } from "./api";

export function EnvironmentListPage() {
  const navigate = useNavigate();
  const { toast } = useToast();
  const [system, setSystem] = React.useState("");
  const [environment, setEnvironment] = React.useState("");
  const [filter, setFilter] = React.useState<{ system?: string; environment?: string }>({});
  const { data, isLoading } = useEnvironments(filter);
  const del = useDeleteEnvironment();

  const [revealed, setRevealed] = React.useState<Set<string>>(new Set());
  const [toDelete, setToDelete] = React.useState<EnvironmentRecord | null>(null);

  const keyOf = (r: EnvironmentRecord) => `${r.system}::${r.environment}`;

  function toggleReveal(k: string) {
    setRevealed((prev) => {
      const next = new Set(prev);
      next.has(k) ? next.delete(k) : next.add(k);
      return next;
    });
  }

  function applyFilter(e: React.FormEvent) {
    e.preventDefault();
    setFilter({ system: system || undefined, environment: environment || undefined });
  }

  async function confirmDelete() {
    if (!toDelete) return;
    try {
      await del.mutateAsync({ system: toDelete.system, environment: toDelete.environment });
      toast({ variant: "success", title: "Record deleted" });
    } catch (err) {
      toast({ variant: "destructive", title: "Delete failed", description: String(err) });
    } finally {
      setToDelete(null);
    }
  }

  return (
    <div>
      <PageHeader
        title="Environment Settings"
        description={data ? `${data.length} record(s)` : undefined}
        actions={
          <Button size="sm" onClick={() => navigate("/environments/new")}>
            <Plus /> New Entry
          </Button>
        }
      />

      <form onSubmit={applyFilter} className="mb-4 flex flex-wrap items-center gap-2">
        <Input
          className="w-48"
          placeholder="Filter by System…"
          value={system}
          onChange={(e) => setSystem(e.target.value)}
        />
        <Input
          className="w-48"
          placeholder="Filter by Environment…"
          value={environment}
          onChange={(e) => setEnvironment(e.target.value)}
        />
        <Button type="submit">Filter</Button>
        <Button
          type="button"
          variant="outline"
          onClick={() => {
            setSystem("");
            setEnvironment("");
            setFilter({});
          }}
        >
          Clear
        </Button>
      </form>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>System</TableHead>
              <TableHead>Environment</TableHead>
              <TableHead>Value</TableHead>
              <TableHead className="w-32" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {isLoading ? (
              <TableRow>
                <TableCell colSpan={4} className="py-8 text-center text-muted-foreground">
                  Loading…
                </TableCell>
              </TableRow>
            ) : !data || data.length === 0 ? (
              <TableRow>
                <TableCell colSpan={4} className="py-8 text-center text-muted-foreground">
                  No environment records found.
                </TableCell>
              </TableRow>
            ) : (
              data.map((r) => {
                const k = keyOf(r);
                const shown = revealed.has(k);
                return (
                  <TableRow key={k}>
                    <TableCell>
                      <Badge variant="secondary">{r.system}</Badge>
                    </TableCell>
                    <TableCell>{r.environment}</TableCell>
                    <TableCell>
                      <div className="flex items-start gap-2">
                        <pre className="max-w-md flex-1 overflow-hidden whitespace-pre-wrap break-all font-mono text-xs">
                          {shown ? r.value : "••••••••••••••••"}
                        </pre>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="h-6 w-6"
                          onClick={() => toggleReveal(k)}
                          title="Show/Hide value"
                        >
                          {shown ? <EyeOff /> : <Eye />}
                        </Button>
                      </div>
                    </TableCell>
                    <TableCell className="text-right">
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={() =>
                          navigate(
                            `/environments/${encodeURIComponent(r.system)}/${encodeURIComponent(r.environment)}/edit`,
                          )
                        }
                      >
                        <Pencil /> Edit
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        className="ml-1 text-destructive"
                        onClick={() => setToDelete(r)}
                      >
                        <Trash2 />
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })
            )}
          </TableBody>
        </Table>
      </div>

      <Dialog open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete environment record</DialogTitle>
            <DialogDescription>
              Delete {toDelete?.system} / {toDelete?.environment}? This cannot be undone.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setToDelete(null)}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={confirmDelete} disabled={del.isPending}>
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
