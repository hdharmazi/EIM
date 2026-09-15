import * as React from "react";
import { useParams } from "react-router-dom";
import { Plus, Trash2 } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import {
  ALL_VERBS,
  GRANTABLE_VERBS,
  useGatewayConsumer,
  useGatewayEntityCatalog,
  useGatewayPermissions,
  useGrantPermission,
  useRevokePermission,
  type GatewayPermission,
  type GatewayResourceType,
  type GatewayVerb,
} from "./api";

export function GatewayPermissionsPage() {
  const { consumerId = "" } = useParams();
  const { toast } = useToast();

  const [environment, setEnvironment] = React.useState("DEV");
  const [environmentInput, setEnvironmentInput] = React.useState("DEV");

  const { data: consumer } = useGatewayConsumer(consumerId, Boolean(consumerId));
  const { data: permissions, isLoading } = useGatewayPermissions(consumerId, environment);
  const grant = useGrantPermission(consumerId);
  const revoke = useRevokePermission(consumerId);

  const catalog = useGatewayEntityCatalog(
    consumer?.systemProfile ?? "",
    environment,
    Boolean(consumer?.systemProfile && environment),
  );

  const [entityDraft, setEntityDraft] = React.useState("");
  const [serviceDraft, setServiceDraft] = React.useState("");
  const [actionDraft, setActionDraft] = React.useState("");

  // One visual row per resource; the verb columns are the individual grants behind it.
  const rowsByType = React.useMemo(() => {
    const map: Record<GatewayResourceType, Map<string, Set<GatewayVerb>>> = {
      DATA: new Map(),
      ACTION: new Map(),
      SERVICE: new Map(),
    };

    for (const p of permissions ?? []) {
      const bucket = map[p.resourceType];
      if (!bucket.has(p.resource)) bucket.set(p.resource, new Set());
      bucket.get(p.resource)!.add(p.verb);
    }

    return map;
  }, [permissions]);

  async function toggle(resourceType: GatewayResourceType, resource: string, verb: GatewayVerb, on: boolean) {
    const input = { environment, resourceType, resource, verb };
    try {
      if (on) await grant.mutateAsync(input);
      else await revoke.mutateAsync(input);
    } catch (err) {
      toast({ variant: "destructive", title: on ? "Grant failed" : "Revoke failed", description: String(err) });
    }
  }

  async function addResource(resourceType: GatewayResourceType, resource: string, verb: GatewayVerb) {
    if (!resource.trim()) return;
    await toggle(resourceType, resource.trim(), verb, true);
  }

  async function removeResource(resourceType: GatewayResourceType, resource: string, verbs: Set<GatewayVerb>) {
    for (const verb of verbs) {
      await revoke.mutateAsync({ environment, resourceType, resource, verb });
    }
  }

  const isProduction = /PROD/i.test(environment);

  return (
    <div>
      <PageHeader
        title={`Permissions — ${consumerId}`}
        description={consumer ? `Connection profile ${consumer.systemProfile}` : undefined}
      />

      <form
        className="mb-4 flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setEnvironment(environmentInput.trim().toUpperCase());
        }}
      >
        <div>
          <label className="mb-1 block text-sm font-medium">Environment</label>
          <Input value={environmentInput} onChange={(e) => setEnvironmentInput(e.target.value)} />
        </div>
        <Button type="submit" variant="outline">
          Show
        </Button>
      </form>

      {isProduction && (
        <div className="mb-4 rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm">
          This is a production environment. Changes here are applied immediately and are recorded
          against your name.
        </div>
      )}

      {catalog.isError && (
        <div className="mb-4 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          The entity list could not be read from D365 for {environment}. Entity names must be typed,
          and a wrong name produces a grant that never matches.
        </div>
      )}

      <section className="mb-8">
        <h2 className="mb-2 text-sm font-semibold">Data entities</h2>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Entity</TableHead>
              {ALL_VERBS.map((verb) => (
                <TableHead key={verb} className="w-[90px] text-center">
                  {verb}
                </TableHead>
              ))}
              <TableHead className="w-[60px]" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {isLoading && (
              <TableRow>
                <TableCell colSpan={6}>Loading…</TableCell>
              </TableRow>
            )}

            {[...rowsByType.DATA.entries()].map(([resource, verbs]) => (
              <TableRow key={resource}>
                <TableCell className="font-medium">{resource}</TableCell>
                {ALL_VERBS.map((verb) => (
                  <TableCell key={verb} className="text-center">
                    <input
                      type="checkbox"
                      checked={verbs.has(verb)}
                      disabled={!GRANTABLE_VERBS.includes(verb)}
                      title={
                        GRANTABLE_VERBS.includes(verb)
                          ? undefined
                          : "DELETE is modelled but not grantable in this release."
                      }
                      onChange={(e) => toggle("DATA", resource, verb, e.target.checked)}
                    />
                  </TableCell>
                ))}
                <TableCell className="text-right">
                  <Button variant="ghost" size="icon" onClick={() => removeResource("DATA", resource, verbs)}>
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </TableCell>
              </TableRow>
            ))}

            <TableRow>
              <TableCell colSpan={6}>
                <div className="flex items-center gap-2">
                  <Input
                    list="gateway-entity-catalog"
                    value={entityDraft}
                    placeholder={catalog.data ? "Start typing an entity name…" : "Entity name"}
                    onChange={(e) => setEntityDraft(e.target.value)}
                  />
                  <datalist id="gateway-entity-catalog">
                    {catalog.data?.map((name) => (
                      <option key={name} value={name} />
                    ))}
                  </datalist>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={async () => {
                      await addResource("DATA", entityDraft, "GET");
                      setEntityDraft("");
                    }}
                  >
                    <Plus className="mr-2 h-4 w-4" />
                    Add with GET
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </section>

      <section className="mb-8">
        <h2 className="mb-2 text-sm font-semibold">Bound actions</h2>
        <p className="mb-2 text-xs text-muted-foreground">
          Invoked by POST. Granted separately from the entity — reading a sales order and confirming
          one are different privileges.
        </p>
        <ResourceList
          resources={rowsByType.ACTION}
          placeholder="SalesOrderHeadersV2/Confirm"
          draft={actionDraft}
          setDraft={setActionDraft}
          onAdd={async (value) => {
            await addResource("ACTION", value, "POST");
            setActionDraft("");
          }}
          onRemove={(resource, verbs) => removeResource("ACTION", resource, verbs)}
        />
      </section>

      <section>
        <h2 className="mb-2 text-sm font-semibold">Custom services</h2>
        <p className="mb-2 text-xs text-muted-foreground">
          Invoked by POST. Arbitrary X++ — allow-list each operation individually.
        </p>
        <ResourceList
          resources={rowsByType.SERVICE}
          placeholder="WTCServices/CustomerService/CreateCustomer"
          draft={serviceDraft}
          setDraft={setServiceDraft}
          onAdd={async (value) => {
            await addResource("SERVICE", value, "POST");
            setServiceDraft("");
          }}
          onRemove={(resource, verbs) => removeResource("SERVICE", resource, verbs)}
        />
      </section>

      <GrantProvenance permissions={permissions ?? []} />
    </div>
  );
}

function ResourceList({
  resources,
  placeholder,
  draft,
  setDraft,
  onAdd,
  onRemove,
}: {
  resources: Map<string, Set<GatewayVerb>>;
  placeholder: string;
  draft: string;
  setDraft: (value: string) => void;
  onAdd: (value: string) => Promise<void>;
  onRemove: (resource: string, verbs: Set<GatewayVerb>) => void;
}) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Resource</TableHead>
          <TableHead className="w-[90px] text-center">POST</TableHead>
          <TableHead className="w-[60px]" />
        </TableRow>
      </TableHeader>
      <TableBody>
        {[...resources.entries()].map(([resource, verbs]) => (
          <TableRow key={resource}>
            <TableCell className="font-medium">{resource}</TableCell>
            <TableCell className="text-center">
              <input type="checkbox" checked readOnly />
            </TableCell>
            <TableCell className="text-right">
              <Button variant="ghost" size="icon" onClick={() => onRemove(resource, verbs)}>
                <Trash2 className="h-4 w-4" />
              </Button>
            </TableCell>
          </TableRow>
        ))}
        <TableRow>
          <TableCell colSpan={3}>
            <div className="flex items-center gap-2">
              <Input value={draft} placeholder={placeholder} onChange={(e) => setDraft(e.target.value)} />
              <Button type="button" variant="outline" onClick={() => onAdd(draft)}>
                <Plus className="mr-2 h-4 w-4" />
                Grant
              </Button>
            </div>
          </TableCell>
        </TableRow>
      </TableBody>
    </Table>
  );
}

function GrantProvenance({ permissions }: { permissions: GatewayPermission[] }) {
  if (permissions.length === 0) return null;

  return (
    <details className="mt-8">
      <summary className="cursor-pointer text-sm font-medium">Grant history</summary>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Resource</TableHead>
            <TableHead>Verb</TableHead>
            <TableHead>Granted by</TableHead>
            <TableHead>When</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {permissions.map((p) => (
            <TableRow key={`${p.resourceType}:${p.resource}:${p.verb}`}>
              <TableCell>{p.resource}</TableCell>
              <TableCell>{p.verb}</TableCell>
              <TableCell>{p.createdBy ?? "—"}</TableCell>
              <TableCell>{p.createdOn ? new Date(p.createdOn).toLocaleString() : "—"}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </details>
  );
}
