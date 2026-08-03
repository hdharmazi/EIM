import * as React from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Pencil, Trash2 } from "lucide-react";
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
import { useConfigurations, useDeleteConfiguration, type ConfigurationRecord } from "./api";

const DEFAULT_CATEGORY = "SimulationEnvironment";

export function ConfigurationListPage() {
  const navigate = useNavigate();
  const { toast } = useToast();
  const [categoryInput, setCategoryInput] = React.useState(DEFAULT_CATEGORY);
  const [category, setCategory] = React.useState(DEFAULT_CATEGORY);
  const { data, isLoading } = useConfigurations(category);
  const del = useDeleteConfiguration();

  const [toDelete, setToDelete] = React.useState<ConfigurationRecord | null>(null);

  function applyFilter(e: React.FormEvent) {
    e.preventDefault();
    setCategory(categoryInput.trim() || DEFAULT_CATEGORY);
  }

  async function confirmDelete() {
    if (!toDelete) return;
    try {
      await del.mutateAsync({ category: toDelete.category, name: toDelete.name });
      toast({ variant: "success", title: "Configuration deleted" });
    } catch (err) {
      toast({ variant: "destructive", title: "Delete failed", description: String(err) });
    } finally {
      setToDelete(null);
    }
  }

  return (
    <div>
      <PageHeader
        title="Configuration"
        description={data ? `${data.length} configuration(s) in ${category}` : undefined}
        actions={
          <Button size="sm" onClick={() => navigate(`/configuration/new?category=${encodeURIComponent(category)}`)}>
            <Plus /> New Configuration
          </Button>
        }
      />

      <form onSubmit={applyFilter} className="mb-4 flex flex-wrap items-center gap-2">
        <Input
          className="w-64"
          placeholder="Category…"
          value={categoryInput}
          onChange={(e) => setCategoryInput(e.target.value)}
        />
        <Button type="submit">Filter</Button>
        <Button
          type="button"
          variant="outline"
          onClick={() => {
            setCategoryInput(DEFAULT_CATEGORY);
            setCategory(DEFAULT_CATEGORY);
          }}
        >
          Reset
        </Button>
      </form>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Display Name</TableHead>
              <TableHead>Fields</TableHead>
              <TableHead className="w-20">Active</TableHead>
              <TableHead className="w-32" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {isLoading ? (
              <TableRow>
                <TableCell colSpan={5} className="py-8 text-center text-muted-foreground">
                  Loading…
                </TableCell>
              </TableRow>
            ) : !data || data.length === 0 ? (
              <TableRow>
                <TableCell colSpan={5} className="py-8 text-center text-muted-foreground">
                  No configurations found in this category.
                </TableCell>
              </TableRow>
            ) : (
              data.map((r) => (
                <TableRow key={`${r.category}::${r.name}`}>
                  <TableCell>
                    <Badge variant="secondary">{r.name}</Badge>
                  </TableCell>
                  <TableCell>{r.displayName ?? ""}</TableCell>
                  <TableCell>
                    <span className="font-mono text-xs text-muted-foreground">
                      {Object.keys(r.fields ?? {}).join(", ") || "—"}
                    </span>
                  </TableCell>
                  <TableCell>
                    {r.isActive ? (
                      <Badge variant="success">Active</Badge>
                    ) : (
                      <Badge variant="warning">Inactive</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        navigate(
                          `/configuration/${encodeURIComponent(r.category)}/${encodeURIComponent(r.name)}/edit`,
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
              ))
            )}
          </TableBody>
        </Table>
      </div>

      <Dialog open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete configuration</DialogTitle>
            <DialogDescription>
              Delete {toDelete?.category} / {toDelete?.name}? This cannot be undone.
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
