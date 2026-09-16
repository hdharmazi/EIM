import * as React from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Pencil, Trash2, KeyRound, ShieldCheck, ShieldAlert, ShieldQuestion } from "lucide-react";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useToast } from "@/components/ui/toast";
import {
  useDeleteGatewayConsumer,
  useGatewayConsumers,
  useGatewayTargets,
  useSetGatewayConsumerKey,
  type GatewayConsumer,
} from "./api";

export function GatewayConsumerListPage() {
  const navigate = useNavigate();
  const { toast } = useToast();
  const { data, isLoading } = useGatewayConsumers();
  const { data: targets } = useGatewayTargets();
  const del = useDeleteGatewayConsumer();
  const setKey = useSetGatewayConsumerKey();

  const [toDelete, setToDelete] = React.useState<GatewayConsumer | null>(null);
  const [keyFor, setKeyFor] = React.useState<GatewayConsumer | null>(null);
  const [keyValue, setKeyValue] = React.useState("");
  const [gatewayApp, setGatewayApp] = React.useState("");

  // Preselect when there is only one gateway, so the common case needs no decision.
  React.useEffect(() => {
    if (targets?.length === 1) setGatewayApp(targets[0].name);
  }, [targets]);

  const selectedTarget = targets?.find((t) => t.name === gatewayApp);

  // A consumer with its own address is verified there, so there is nothing to choose.
  const consumerGatewayUrl = keyFor?.gatewayUrl?.trim() || null;

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

  function closeKeyDialog() {
    setKeyFor(null);
    // The raw key should not outlive the dialog, even in component state.
    setKeyValue("");
  }

  async function confirmSetKey() {
    if (!keyFor) return;
    try {
      const result = await setKey.mutateAsync({
        consumerId: keyFor.consumerId,
        key: keyValue,
        gatewayApp: gatewayApp || undefined,
      });
      toast({
        variant: result.verified ? "success" : "warning",
        title: result.verified ? "Key verified and registered" : "Key stored without verification",
        description: result.detail,
      });
      closeKeyDialog();
    } catch (err) {
      // Left open so a mistyped key can be corrected without reopening. The message distinguishes a
      // key the gateway refused from a gateway that could not be reached - conflating them would
      // send an administrator hunting for a fault in a perfectly good key.
      toast({ variant: "destructive", title: "Key not registered", description: String(err) });
    }
  }

  function identityCell(consumer: GatewayConsumer) {
    if (!consumer.identityVerified) {
      return (
        <span
          className="inline-flex items-center gap-1 text-sm text-amber-700"
          title="Identity comes from the X-Consumer-Id header and is not proven. Only one consumer may be enabled in this state."
        >
          <ShieldAlert className="h-4 w-4" /> Unverified
        </span>
      );
    }

    // A hash with no verification record is not the same as a verified key: it predates verification
    // or was stored against a local gateway. Showing both as "Verified" is the exact false assurance
    // this feature exists to remove.
    if (!consumer.verifiedOn) {
      return (
        <span
          className="inline-flex items-center gap-1 text-sm text-muted-foreground"
          title="A key is registered but was never checked against a gateway. Re-register it to verify."
        >
          <ShieldQuestion className="h-4 w-4" /> Key set, unchecked
        </span>
      );
    }

    return (
      <span className="inline-flex flex-col text-sm">
        <span className="inline-flex items-center gap-1">
          <ShieldCheck className="h-4 w-4" /> Verified {new Date(consumer.verifiedOn).toLocaleDateString()}
        </span>
        {consumer.verifiedApp && (
          <span className="text-xs text-muted-foreground">{consumer.verifiedApp}</span>
        )}
      </span>
    );
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
          Function keys are created in Azure, then registered here with <strong>Set key</strong> so the
          gateway can tell consumers apart. Only a hash is kept — the key is never stored or shown, so
          give it to the consumer directly. To revoke access immediately, delete the key in the Azure
          portal; that takes effect at once and does not need a deployment.
        </p>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Consumer</TableHead>
            <TableHead>Connection profile</TableHead>
            <TableHead>Gateway</TableHead>
            <TableHead>Enabled</TableHead>
            <TableHead>Identity</TableHead>
            <TableHead>Expires</TableHead>
            <TableHead>Created by</TableHead>
            <TableHead className="w-[240px]" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {isLoading && (
            <TableRow>
              <TableCell colSpan={8}>Loading…</TableCell>
            </TableRow>
          )}

          {data?.length === 0 && (
            <TableRow>
              <TableCell colSpan={8}>No consumers yet.</TableCell>
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
              <TableCell className="max-w-[220px] break-all text-xs">
                {consumer.gatewayUrl ?? (
                  <span className="text-muted-foreground">{targets?.[0]?.name ?? "Not configured"}</span>
                )}
              </TableCell>
              <TableCell>
                <Badge variant={consumer.enabled ? "default" : "secondary"}>
                  {consumer.enabled ? "Enabled" : "Disabled"}
                </Badge>
              </TableCell>
              <TableCell>{identityCell(consumer)}</TableCell>
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
                <Button variant="ghost" size="sm" onClick={() => setKeyFor(consumer)}>
                  Set key
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

      <Dialog open={keyFor !== null} onOpenChange={(open) => !open && closeKeyDialog()}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Set function key</DialogTitle>
            <DialogDescription>
              Paste the Azure function key issued to {keyFor?.consumerId}. It is checked against the
              gateway before being saved, then hashed — it cannot be read back afterwards, so keep
              your own copy before closing this dialog.
              {keyFor?.identityVerified &&
                " This replaces the key already registered: the current key stops being accepted as soon as you save."}
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-2">
            <Label htmlFor="gateway-consumer-app">Verify against</Label>
            {consumerGatewayUrl ? (
              <>
                <p className="break-all text-sm">{consumerGatewayUrl}</p>
                <p className="text-xs text-muted-foreground">
                  This consumer's own gateway address. Change it on the consumer's edit screen.
                </p>
              </>
            ) : targets && targets.length > 1 ? (
              <select
                id="gateway-consumer-app"
                className="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm"
                value={gatewayApp}
                onChange={(e) => setGatewayApp(e.target.value)}
                disabled={setKey.isPending}
              >
                <option value="">Choose a gateway…</option>
                {targets.map((t) => (
                  <option key={t.name} value={t.name}>
                    {t.name}
                  </option>
                ))}
              </select>
            ) : (
              <p className="text-sm">{targets?.[0]?.name ?? "No gateway configured"}</p>
            )}
            {!consumerGatewayUrl && selectedTarget && (
              <p className="break-all text-xs text-muted-foreground">
                {selectedTarget.baseUrl} — this is also the base address the consumer calls.
              </p>
            )}
          </div>

          <div className="space-y-2">
            <Label htmlFor="gateway-consumer-key">Function key</Label>
            <Input
              id="gateway-consumer-key"
              type="password"
              autoComplete="off"
              value={keyValue}
              onChange={(e) => setKeyValue(e.target.value)}
              placeholder="Paste the key from the Azure portal"
              disabled={setKey.isPending}
            />
            {setKey.isPending && (
              <p className="text-xs text-muted-foreground">
                Checking the key against the gateway. This can take a few seconds if the gateway has
                scaled to zero.
              </p>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={closeKeyDialog}>
              Cancel
            </Button>
            <Button
              onClick={confirmSetKey}
              disabled={keyValue.trim().length === 0 || (!consumerGatewayUrl && !gatewayApp) || setKey.isPending}
            >
              {setKey.isPending ? "Saving…" : "Save key"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

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
