import * as React from "react";
import { useNavigate, useParams } from "react-router-dom";
import { KeyRound } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import {
  useCreateGatewayConsumer,
  useGatewayConsumer,
  useGatewayTargets,
  useUpdateGatewayConsumer,
  type GatewayConsumerInput,
} from "./api";

export function GatewayConsumerFormPage() {
  const { consumerId } = useParams();
  const isEdit = Boolean(consumerId);
  const navigate = useNavigate();
  const { toast } = useToast();

  const { data: existing } = useGatewayConsumer(consumerId ?? "", isEdit);
  const { data: targets } = useGatewayTargets();
  const create = useCreateGatewayConsumer();
  const update = useUpdateGatewayConsumer();

  const [form, setForm] = React.useState<GatewayConsumerInput>({
    consumerId: "",
    systemProfile: "D365FSC-GW",
    enabled: false,
    expiresOn: null,
    description: "",
    gatewayUrl: "",
  });

  React.useEffect(() => {
    if (!existing) return;
    setForm({
      consumerId: existing.consumerId,
      systemProfile: existing.systemProfile,
      enabled: existing.enabled,
      expiresOn: existing.expiresOn ? existing.expiresOn.slice(0, 10) : null,
      description: existing.description ?? "",
      gatewayUrl: existing.gatewayUrl ?? "",
    });
  }, [existing]);

  // Shown as the placeholder so the fallback is visible rather than implied by an empty field.
  const defaultGateway = targets?.[0];

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try {
      if (isEdit) {
        await update.mutateAsync(form);
      } else {
        await create.mutateAsync(form);
      }
      toast({ variant: "success", title: isEdit ? "Consumer updated" : "Consumer created" });
      navigate("/gateway/consumers");
    } catch (err) {
      toast({ variant: "destructive", title: "Save failed", description: String(err) });
    }
  }

  return (
    <div>
      <PageHeader
        title={isEdit ? `Edit ${consumerId}` : "New gateway consumer"}
        description="Consumers are denied everything until permissions are granted."
      />

      <form onSubmit={submit} className="max-w-xl space-y-4">
        <div>
          <label className="mb-1 block text-sm font-medium">Consumer id</label>
          <Input
            value={form.consumerId}
            onChange={(e) => setForm({ ...form, consumerId: e.target.value })}
            disabled={isEdit}
            placeholder="acme-erp"
            required
          />
          <p className="mt-1 text-xs text-muted-foreground">
            Sent by the caller as the <code>X-Consumer-Id</code> header. Not a secret.
          </p>
        </div>

        <div>
          <label className="mb-1 block text-sm font-medium">Connection profile</label>
          <Input
            value={form.systemProfile}
            onChange={(e) => setForm({ ...form, systemProfile: e.target.value })}
            required
          />
          <p className="mt-1 text-xs text-muted-foreground">
            The <code>SYSTEM</code> value in Environment Settings whose credentials this consumer's
            traffic uses. Callers cannot choose this.
          </p>
        </div>

        <div>
          <label className="mb-1 block text-sm font-medium">Gateway address</label>
          <Input
            type="url"
            value={form.gatewayUrl ?? ""}
            onChange={(e) => setForm({ ...form, gatewayUrl: e.target.value })}
            placeholder={defaultGateway?.baseUrl ?? "https://my-gateway.azurewebsites.net"}
          />
          <p className="mt-1 text-xs text-muted-foreground">
            The gateway this consumer calls, and the one their key is checked against. Leave blank to
            use {defaultGateway ? <code>{defaultGateway.name}</code> : "the gateway this dashboard is configured with"}.
            Must be an <code>https</code> address on a permitted host.
          </p>
        </div>

        <div>
          <label className="mb-1 block text-sm font-medium">Description</label>
          <Input
            value={form.description ?? ""}
            onChange={(e) => setForm({ ...form, description: e.target.value })}
          />
        </div>

        <div>
          <label className="mb-1 block text-sm font-medium">Expires on</label>
          <Input
            type="date"
            value={form.expiresOn ?? ""}
            onChange={(e) => setForm({ ...form, expiresOn: e.target.value || null })}
          />
        </div>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={(e) => setForm({ ...form, enabled: e.target.checked })}
          />
          Enabled
        </label>

        <div className="flex items-start gap-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          <KeyRound className="mt-0.5 h-4 w-4 shrink-0" />
          <p>
            No key is entered here. Create a function-level key for this consumer in the Azure portal
            and share it with them directly; this screen never sees it.
          </p>
        </div>

        <div className="flex gap-2">
          <Button type="submit">{isEdit ? "Save" : "Create"}</Button>
          <Button type="button" variant="outline" onClick={() => navigate("/gateway/consumers")}>
            Cancel
          </Button>
        </div>
      </form>
    </div>
  );
}
