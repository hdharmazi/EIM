import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export type RouteLifecycle = "Paused" | "Draining" | "Live";

export interface RouteRecord {
  environment: string;
  sourceSystem: string;
  sourceEntity: string;
  orchestrationType: string;
  enabled: boolean;
  onPause: string;
  modifiedBy: string | null;
  modifiedDateTime: string | null;
  heldCount: number;
  retryScheduledCount: number;
  oldestHeldUtc: string | null;
  lifecycle: RouteLifecycle;
}

export interface RouteKey {
  environment: string;
  sourceSystem: string;
  sourceEntity: string;
}

export function routeKeyOf(route: RouteRecord): RouteKey {
  return {
    environment: route.environment,
    sourceSystem: route.sourceSystem,
    sourceEntity: route.sourceEntity,
  };
}

export function routeKeyString(key: RouteKey): string {
  return `${key.environment}/${key.sourceSystem}/${key.sourceEntity}`;
}

export interface OrphanedBacklog {
  environment: string;
  sourceSystem: string;
  sourceEntity: string;
  count: number;
  oldestUtc: string | null;
}

export function useRoutes() {
  return useQuery({
    queryKey: ["routes"],
    queryFn: () => apiFetch<RouteRecord[]>("/api/routes"),
    // Held counts move while a route drains, so the console refreshes itself.
    refetchInterval: 15_000,
  });
}

export function useOrphanedBacklog() {
  return useQuery({
    queryKey: ["routes", "orphans"],
    queryFn: () => apiFetch<OrphanedBacklog[]>("/api/routes/orphans"),
    refetchInterval: 60_000,
  });
}

function useRouteControl(action: "pause" | "resume") {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: RouteKey) =>
      apiFetch<{ success: boolean; message: string }>(`/api/routes/${action}`, {
        method: "POST",
        body: key,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["routes"] });
      qc.invalidateQueries({ queryKey: ["messages"] });
    },
  });
}

export const usePauseRoute = () => useRouteControl("pause");
export const useResumeRoute = () => useRouteControl("resume");

/**
 * Other routes whose traffic plausibly depends on this one. Both directions of the same entity
 * share a business record, so pausing one side while the other keeps writing is worth warning
 * about — the reverse-direction write will not generate a corrective event back.
 */
export function dependentRoutes(route: RouteRecord, all: RouteRecord[]): RouteRecord[] {
  const stem = entityStem(route.sourceEntity);
  if (!stem) return [];

  return all.filter(
    (r) =>
      r.environment === route.environment &&
      !(r.sourceSystem === route.sourceSystem && r.sourceEntity === route.sourceEntity) &&
      entityStem(r.sourceEntity) === stem,
  );
}

/**
 * Reduces both naming vocabularies to a comparable stem: Capture uses short names like
 * "CustomerAddress", D365 uses logical names like "mserp_wtc_custpostaladdressv2entity".
 */
function entityStem(entity: string): string | null {
  const known: Record<string, string> = {
    customer: "customer",
    contact: "contact",
    customeraddress: "address",
    partycontact: "partycontact",
    mserp_wtc_custcustomerentity: "customer",
    mserp_smmcontactpersonv2entity: "contact",
    mserp_wtc_custpostaladdressv2entity: "address",
    mserp_wtc_custpostaladdressdeletedv2entity: "address",
    mserp_dirpartycontactv3entity: "partycontact",
  };
  return known[entity.toLowerCase()] ?? null;
}
