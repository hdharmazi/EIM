import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export type GatewayResourceType = "DATA" | "ACTION" | "SERVICE";
export type GatewayVerb = "GET" | "POST" | "PATCH" | "DELETE";

/** DELETE is shown so the model is visible, but the API and a CHECK constraint both refuse it. */
export const GRANTABLE_VERBS: GatewayVerb[] = ["GET", "POST", "PATCH"];
export const ALL_VERBS: GatewayVerb[] = ["GET", "POST", "PATCH", "DELETE"];

export interface GatewayConsumer {
  consumerId: string;
  systemProfile: string;
  enabled: boolean;
  expiresOn?: string | null;
  description?: string | null;
  /** True when a key hash is recorded. The hash itself is never sent to the client. */
  identityVerified: boolean;
  /** The gateway app that accepted the key. Null means unknown, never "no app". */
  verifiedApp?: string | null;
  /** When the key was verified. A point in time, not a continuing guarantee. */
  verifiedOn?: string | null;
  /** The gateway base address this consumer calls. Null falls back to the dashboard's own gateway. */
  gatewayUrl?: string | null;
  createdBy?: string | null;
  createdOn?: string | null;
  modifiedBy?: string | null;
  modifiedOn?: string | null;
}

export interface GatewayConsumerInput {
  consumerId: string;
  systemProfile: string;
  enabled: boolean;
  expiresOn?: string | null;
  description?: string | null;
  /** Blank uses the dashboard's configured gateway. Rejected by the server if the host is not allowed. */
  gatewayUrl?: string | null;
}

export interface GatewayPermission {
  consumerId: string;
  environment: string;
  resourceType: GatewayResourceType;
  resource: string;
  verb: GatewayVerb;
  createdBy?: string | null;
  createdOn?: string | null;
}

export interface GatewayPermissionInput {
  environment: string;
  resourceType: GatewayResourceType;
  resource: string;
  verb: GatewayVerb;
}

export function useGatewayConsumers() {
  return useQuery({
    queryKey: ["gateway-consumers"],
    queryFn: () => apiFetch<GatewayConsumer[]>("/api/gateway/consumers"),
  });
}

export function useGatewayConsumer(consumerId: string, enabled: boolean) {
  return useQuery({
    queryKey: ["gateway-consumer", consumerId],
    queryFn: () => apiFetch<GatewayConsumer>(`/api/gateway/consumers/${encodeURIComponent(consumerId)}`),
    enabled,
  });
}

export function useCreateGatewayConsumer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: GatewayConsumerInput) =>
      apiFetch<GatewayConsumer>("/api/gateway/consumers", { method: "POST", body: input }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-consumers"] }),
  });
}

export function useUpdateGatewayConsumer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: GatewayConsumerInput) =>
      apiFetch<void>(`/api/gateway/consumers/${encodeURIComponent(input.consumerId)}`, {
        method: "PUT",
        body: input,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-consumers"] }),
  });
}

export function useDeleteGatewayConsumer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (consumerId: string) =>
      apiFetch<void>(`/api/gateway/consumers/${encodeURIComponent(consumerId)}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-consumers"] }),
  });
}

export interface GatewayTarget {
  name: string;
  baseUrl: string;
}

/** The gateways a key can be verified against. Server-configured; the client only chooses. */
export function useGatewayTargets() {
  return useQuery({
    queryKey: ["gateway-targets"],
    queryFn: () => apiFetch<GatewayTarget[]>("/api/gateway/gateways"),
    staleTime: 5 * 60 * 1000,
  });
}

export interface GatewayKeyResult {
  /** False when the key was stored without being checked, e.g. against a local gateway. */
  verified: boolean;
  detail: string;
  gatewayApp?: string | null;
}

/**
 * Registers a function key. The value is hashed server-side and never stored, returned or logged.
 * The server probes the chosen gateway first and refuses a key it will not accept, so this can fail
 * with a rejection (bad key) or a 503 (gateway unreachable) — which are not the same thing.
 */
export function useSetGatewayConsumerKey() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      consumerId,
      key,
      gatewayApp,
    }: {
      consumerId: string;
      key: string;
      gatewayApp?: string;
    }) =>
      apiFetch<GatewayKeyResult>(`/api/gateway/consumers/${encodeURIComponent(consumerId)}/key`, {
        method: "POST",
        body: { key, gatewayApp },
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-consumers"] }),
  });
}

export function useGatewayPermissions(consumerId: string, environment: string) {
  return useQuery({
    queryKey: ["gateway-permissions", consumerId, environment],
    queryFn: () =>
      apiFetch<GatewayPermission[]>(
        `/api/gateway/consumers/${encodeURIComponent(consumerId)}/permissions?environment=${encodeURIComponent(environment)}`,
      ),
    enabled: Boolean(consumerId && environment),
  });
}

export function useGrantPermission(consumerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: GatewayPermissionInput) =>
      apiFetch<void>(`/api/gateway/consumers/${encodeURIComponent(consumerId)}/permissions`, {
        method: "POST",
        body: input,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-permissions"] }),
  });
}

export function useRevokePermission(consumerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: GatewayPermissionInput) =>
      apiFetch<void>(`/api/gateway/consumers/${encodeURIComponent(consumerId)}/permissions`, {
        method: "DELETE",
        body: input,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["gateway-permissions"] }),
  });
}

/** Entity names offered for selection, read from D365 so the stored spelling is always canonical. */
export function useGatewayEntityCatalog(systemProfile: string, environment: string, enabled: boolean) {
  return useQuery({
    queryKey: ["gateway-catalog", systemProfile, environment],
    queryFn: () =>
      apiFetch<string[]>(
        `/api/gateway/catalog/${encodeURIComponent(systemProfile)}/${encodeURIComponent(environment)}/entities`,
      ),
    enabled,
    staleTime: 60 * 60 * 1000,
    retry: false,
  });
}
