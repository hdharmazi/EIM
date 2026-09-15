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
