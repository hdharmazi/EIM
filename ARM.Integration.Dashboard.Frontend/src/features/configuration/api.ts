import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export interface ConfigurationRecord {
  category: string;
  name: string;
  displayName?: string;
  fields: Record<string, string>;
  isActive: boolean;
}

/** Lists active configurations in a category (used by both the admin screen and selection UIs). */
export function useConfigurations(category: string, enabled = true) {
  return useQuery({
    queryKey: ["configurations", category],
    queryFn: () =>
      apiFetch<ConfigurationRecord[]>(`/api/configuration?category=${encodeURIComponent(category)}`),
    enabled: enabled && !!category,
  });
}

export function useConfiguration(category: string, name: string, enabled: boolean) {
  return useQuery({
    queryKey: ["configuration", category, name],
    queryFn: () =>
      apiFetch<ConfigurationRecord>(
        `/api/configuration/${encodeURIComponent(category)}/${encodeURIComponent(name)}`,
      ),
    enabled,
  });
}

export function useCreateConfiguration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (record: ConfigurationRecord) =>
      apiFetch<ConfigurationRecord>("/api/configuration", { method: "POST", body: record }),
    onSuccess: (_data, record) =>
      qc.invalidateQueries({ queryKey: ["configurations", record.category] }),
  });
}

export function useUpdateConfiguration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (record: ConfigurationRecord) =>
      apiFetch<void>(
        `/api/configuration/${encodeURIComponent(record.category)}/${encodeURIComponent(record.name)}`,
        { method: "PUT", body: record },
      ),
    onSuccess: (_data, record) =>
      qc.invalidateQueries({ queryKey: ["configurations", record.category] }),
  });
}

export function useDeleteConfiguration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: { category: string; name: string }) =>
      apiFetch<void>(
        `/api/configuration/${encodeURIComponent(key.category)}/${encodeURIComponent(key.name)}`,
        { method: "DELETE" },
      ),
    onSuccess: (_data, key) =>
      qc.invalidateQueries({ queryKey: ["configurations", key.category] }),
  });
}
