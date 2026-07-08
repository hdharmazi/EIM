import * as React from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Pencil, Trash2 } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
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
import { formatDateTime } from "@/lib/format";
import { useDeleteUser, useUsers, type AppUser } from "./api";

function roleVariant(role: string) {
  if (role === "Admin") return "destructive" as const;
  if (role === "Editor") return "default" as const;
  return "secondary" as const;
}

export function UserListPage() {
  const navigate = useNavigate();
  const { toast } = useToast();
  const { data, isLoading } = useUsers();
  const del = useDeleteUser();
  const [toDelete, setToDelete] = React.useState<AppUser | null>(null);

  async function confirmDelete() {
    if (!toDelete) return;
    try {
      await del.mutateAsync(toDelete.email);
      toast({ variant: "success", title: "User removed" });
    } catch (err) {
      toast({ variant: "destructive", title: "Delete failed", description: String(err) });
    } finally {
      setToDelete(null);
    }
  }

  return (
    <div>
      <PageHeader
        title="User Management"
        description={data ? `${data.length} user(s)` : undefined}
        actions={
          <Button size="sm" onClick={() => navigate("/users/new")}>
            <Plus /> Add User
          </Button>
        }
      />

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Email</TableHead>
              <TableHead>Display Name</TableHead>
              <TableHead>Role</TableHead>
              <TableHead>Active</TableHead>
              <TableHead>Created</TableHead>
              <TableHead className="w-32" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {isLoading ? (
              <TableRow>
                <TableCell colSpan={6} className="py-8 text-center text-muted-foreground">
                  Loading…
                </TableCell>
              </TableRow>
            ) : !data || data.length === 0 ? (
              <TableRow>
                <TableCell colSpan={6} className="py-8 text-center text-muted-foreground">
                  No users configured.
                </TableCell>
              </TableRow>
            ) : (
              data.map((u) => (
                <TableRow key={u.email}>
                  <TableCell className="font-mono text-xs">{u.email}</TableCell>
                  <TableCell>{u.displayName}</TableCell>
                  <TableCell>
                    <Badge variant={roleVariant(u.role)}>{u.role}</Badge>
                  </TableCell>
                  <TableCell>
                    {u.isActive ? (
                      <Badge variant="success">Yes</Badge>
                    ) : (
                      <Badge variant="secondary">No</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {formatDateTime(u.createdDateTime)}
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => navigate(`/users/${encodeURIComponent(u.email)}/edit`)}
                    >
                      <Pencil /> Edit
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="ml-1 text-destructive"
                      onClick={() => setToDelete(u)}
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
            <DialogTitle>Remove user</DialogTitle>
            <DialogDescription>Remove {toDelete?.email}? This cannot be undone.</DialogDescription>
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
