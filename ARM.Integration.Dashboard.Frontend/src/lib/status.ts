import { useQuery } from "@tanstack/react-query";
import type { BadgeProps } from "@/components/ui/badge";
import { apiFetch } from "@/lib/api";

export interface StatusRecord {
  status: number;
  label: string;
}

/**
 * Fallback vocabulary. The catalogue is served from STATUSTABLE so a client can relabel statuses
 * without a frontend deploy, but the values are compiled into the engine, so these stay correct
 * and keep badges readable if that request fails.
 */
export const MESSAGE_STATUSES = [
  { value: 0, label: "NEW" },
  { value: 1, label: "PROCESSING" },
  { value: 2, label: "COMPLETED" },
  { value: 3, label: "FAILED" },
  { value: 4, label: "DEAD" },
  { value: 5, label: "AWAITING D365" },
  { value: 6, label: "HELD" },
  { value: 7, label: "RETRY SCHEDULED" },
] as const;

const STATUS_LABELS: Record<number, string> = Object.fromEntries(
  MESSAGE_STATUSES.map((s) => [s.value, s.label]),
);

export const STATUS_PROCESSING = 1;
export const STATUS_COMPLETED = 2;
export const STATUS_FAILED = 3;
export const STATUS_DEAD_LETTER = 4;
export const STATUS_AWAITING_D365 = 5;
export const STATUS_HELD = 6;
export const STATUS_RETRY_SCHEDULED = 7;

/** Mirrors MessageStatus.Reprocessable on the server. */
export const REPROCESSABLE_STATUSES = [STATUS_FAILED, STATUS_DEAD_LETTER, STATUS_HELD];

export function statusLabel(status: number): string {
  return STATUS_LABELS[status] ?? `UNKNOWN (${status})`;
}

export function statusBadgeVariant(status: number): BadgeProps["variant"] {
  switch (status) {
    case STATUS_COMPLETED:
      return "success";
    case STATUS_FAILED:
    case STATUS_DEAD_LETTER:
      return "destructive";
    case STATUS_AWAITING_D365:
    case STATUS_HELD:
    case STATUS_RETRY_SCHEDULED:
      return "warning";
    case STATUS_PROCESSING:
      return "default";
    default:
      return "secondary";
  }
}

export function isErrorStatus(status: number): boolean {
  return status === STATUS_FAILED || status === STATUS_DEAD_LETTER;
}

export function canReprocess(status: number): boolean {
  return REPROCESSABLE_STATUSES.includes(status);
}

/** Labels from STATUSTABLE, falling back to the built-in vocabulary. */
export function useStatusCatalogue() {
  const query = useQuery({
    queryKey: ["statuses"],
    queryFn: () => apiFetch<StatusRecord[]>("/api/statuses"),
    staleTime: 5 * 60 * 1000,
  });

  const labels: Record<number, string> = { ...STATUS_LABELS };
  for (const s of query.data ?? []) {
    labels[s.status] = s.label.toUpperCase();
  }

  return {
    ...query,
    label: (status: number) => labels[status] ?? `UNKNOWN (${status})`,
    options: Object.entries(labels)
      .map(([value, label]) => ({ value: Number(value), label }))
      .sort((a, b) => a.value - b.value),
  };
}
