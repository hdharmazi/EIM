import * as React from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Pencil, Trash2, KeyRound, ShieldCheck, ShieldAlert } from "lucide-react";
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
import { useDeleteGatewayConsumer, useGatewayConsumers, type GatewayConsumer } from "./api";

export function GatewayConsumerListPage() {
  const navigate = useNavigate();
  const { toast } = useToast();
  const { data, isLoading } = useGatewayConsumers();
  const del = useDeleteGatewayConsumer();

  const [toDelete, setToDelete] = React.useState<GatewayConsumer | null>(null);

  async function confirmDelete() {
    if (!toDelete) return;
    try {
      await del.mutateAsync(toDelete.consumerId);
      toast({ variant: "success", title: "Consumer deleted", description: "Its grants were removed too." });
    } catch (err) {
      toast({ variant: "destructive", title: "Delete failed", description: String(err) });
    } finally {
      setToDelete(null);
    }
  }

  function expiryLabel(consumer: GatewayConsumer) {
    if (!consumer.expiresOn) return "—";
    const expires = new Date(consumer.expiresOn);
    const expired = expires.getTime() <= Date.now();
    return (
      <span className={expired ? "text-destructive" : undefined}>
        {expires.toLocaleDateString()}
        {expired ? " (expired)" : ""}
      </span>
    );
  }

  return (
    <div>
      <PageHeader
        title="Gateway Consumers"
        description={data ? `${data.length} consumer(s)` : undefined}
        actions={
          <Button onClick={() => navigate("/gateway/consumers/new")}>
            <Plus className="mr-2 h-4 w-4" />
            New consumer
          </Button>
        }
      />

      <div className="mb-4 flex items-start gap-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
        <KeyRound className="mt-0.5 h-4 w-4 shrink-0" />
        <p>
          Function keys are managed in Azure, not here. Create a function-level key per consumer and
          give it to them directly. To revoke access immediately, delete the key in the Azure portal —
          that takes effect at once and does not need a deployment.
        </p>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Consumer</TableHead>
            <TableHead>Connection profile</TableHead>
            <TableHead>Enabled</TableHead>
            <TableHead>Identity</TableHead>
            <TableHead>Expires</TableHead>
            <TableHead>Created by</TableHead>
            <TableHead className="w-[160px]" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {isLoading && (
            <TableRow>
              <TableCell colSpan={7}>Loading…</TableCell>
            </TableRow>
          )}

          {data?.length === 0 && (
            <TableRow>
              <TableCell colSpan={7}>No consumers yet.</TableCell>
            </TableRow>
          )}

          {data?.map((consumer) => (
            <TableRow key={consumer.consumerId}>
              <TableCell className="font-medium">
                {consumer.consumerId}
                {consumer.description && (
                  <div className="text-xs text-muted-foreground">{consumer.description}</div>
                )}
              </TableCell>
              <TableCell>{consumer.systemProfile}</TableCell>
              <TableCell>
                <Badge variant={consumer.enabled ? "default" : "secondary"}>
                  {consumer.enabled ? "Enabled" : "Disabled"}
                </Badge>
              </TableCell>
              <TableCell>
                {consumer.identityVerified ? (
                  <span className="inline-flex items-center gap-1 text-sm">
                    <ShieldCheck className="h-4 w-4" /> Verified
                  </span>
                ) : (
                  <span
                    className="inline-flex items-center gap-1 text-sm text-amber-700"
                    title="Identity comes from the X-Consumer-Id header and is not proven. Only one consumer may be enabled in this state."
                  >
                    <ShieldAlert className="h-4 w-4" /> Unverified
                  </span>
                )}
              </TableCell>
              <TableCell>{expiryLabel(consumer)}</TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {consumer.createdBy ?? "—"}
                {consumer.createdOn && <div>{new Date(consumer.createdOn).toLocaleString()}</div>}
              </TableCell>
              <TableCell className="text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => navigate(`/gateway/consumers/${encodeURIComponent(consumer.consumerId)}/permissions`)}
                >
                  Permissions
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => navigate(`/gateway/consumers/${encodeURIComponent(consumer.consumerId)}/edit`)}
                >
                  <Pencil className="h-4 w-4" />
                </Button>
                <Button variant="ghost" size="icon" onClick={() => setToDelete(consumer)}>
                  <Trash2 className="h-4 w-4" />
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <Dialog open={toDelete !== null} onOpenChange={(open) => !open && setToDelete(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete consumer</DialogTitle>
            <DialogDescription>
              {toDelete?.consumerId} and all of its permission grants will be removed. The function
              key in Azure is not deleted by this — remove it there as well.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setToDelete(null)}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={confirmDelete}>
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
