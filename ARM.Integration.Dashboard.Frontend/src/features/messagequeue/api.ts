import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export interface MessageQueueRecord {
  id: number;
  sourceId: string;
  sourceSystem: string;
  sourceEntity: string;
  destinationSystem: string;
  destinationEntity: string;
  type: string;
  environment: string;
  queue: string;
  responseQueue: string;
  payload: string;
  messageLog: string;
  status: number;
  createdAt: string | null;
  modifiedDateTime: string | null;
  retryCount: number;
  nextAttemptDateTime: string | null;
}

export interface MessageLogDetail {
  id: number;
  messageId: number;
  name: string;
  detail: string | null;
  createdDateTime: string;
}

export interface MessageListParams {
  status?: number;
  search?: string;
  top?: number;
}

export function useMessages(params: MessageListParams) {
  const query = new URLSearchParams();
  if (params.status !== undefined) query.set("status", String(params.status));
  if (params.search) query.set("search", params.search);
  if (params.top) query.set("top", String(params.top));
  const qs = query.toString();
  return useQuery({
    queryKey: ["messages", params],
    queryFn: () => apiFetch<MessageQueueRecord[]>(`/api/messagequeue${qs ? `?${qs}` : ""}`),
  });
}

export function useStatusCounts() {
  return useQuery({
    queryKey: ["messages", "status-counts"],
    queryFn: () => apiFetch<Record<string, number>>("/api/messagequeue/status-counts"),
  });
}

export function useRecentFailed(top = 10) {
  return useQuery({
    queryKey: ["messages", "recent-failed", top],
    queryFn: () => apiFetch<MessageQueueRecord[]>(`/api/messagequeue/recent-failed?top=${top}`),
  });
}

export function useMessage(id: number) {
  return useQuery({
    queryKey: ["message", id],
    queryFn: () => apiFetch<MessageQueueRecord>(`/api/messagequeue/${id}`),
    enabled: Number.isFinite(id),
  });
}

export function useLogDetails(id: number) {
  return useQuery({
    queryKey: ["message", id, "log-details"],
    queryFn: () => apiFetch<MessageLogDetail[]>(`/api/messagequeue/${id}/log-details`),
    enabled: Number.isFinite(id),
  });
}

export function useUpdatePayload(id: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: string) =>
      apiFetch<void>(`/api/messagequeue/${id}/payload`, { method: "PUT", body: payload }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["message", id] }),
  });
}

export interface ReprocessResult {
  success: boolean;
  message: string;
}

export function useReprocess(id: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiFetch<ReprocessResult>(`/api/messagequeue/${id}/reprocess`, { method: "POST" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["message", id] }),
  });
}

export interface CleanupResult {
  success: boolean;
  deleted: number;
  message: string;
}

export function useCleanup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (params: { status?: number; olderThanDays?: number }) => {
      const q = new URLSearchParams();
      if (params.status !== undefined) q.set("status", String(params.status));
      if (params.olderThanDays !== undefined) q.set("olderThanDays", String(params.olderThanDays));
      return apiFetch<CleanupResult>(`/api/messagequeue/cleanup?${q.toString()}`, { method: "POST" });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["messages"] }),
  });
}
