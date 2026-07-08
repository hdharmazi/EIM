import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export interface EnvironmentRecord {
  system: string;
  environment: string;
  value: string;
}

export function useEnvironments(filter: { system?: string; environment?: string }) {
  const q = new URLSearchParams();
  if (filter.system) q.set("system", filter.system);
  if (filter.environment) q.set("environment", filter.environment);
  const qs = q.toString();
  return useQuery({
    queryKey: ["environments", filter],
    queryFn: () => apiFetch<EnvironmentRecord[]>(`/api/environment${qs ? `?${qs}` : ""}`),
  });
}

export function useEnvironment(system: string, environment: string, enabled: boolean) {
  return useQuery({
    queryKey: ["environment", system, environment],
    queryFn: () =>
      apiFetch<EnvironmentRecord>(
        `/api/environment/${encodeURIComponent(system)}/${encodeURIComponent(environment)}`,
      ),
    enabled,
  });
}

export function useCreateEnvironment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (record: EnvironmentRecord) =>
      apiFetch<EnvironmentRecord>("/api/environment", { method: "POST", body: record }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["environments"] }),
  });
}

export function useUpdateEnvironment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (record: EnvironmentRecord) =>
      apiFetch<void>(
        `/api/environment/${encodeURIComponent(record.system)}/${encodeURIComponent(record.environment)}`,
        { method: "PUT", body: record },
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["environments"] }),
  });
}

export function useDeleteEnvironment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: { system: string; environment: string }) =>
      apiFetch<void>(
        `/api/environment/${encodeURIComponent(key.system)}/${encodeURIComponent(key.environment)}`,
        { method: "DELETE" },
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["environments"] }),
  });
}
