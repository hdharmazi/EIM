import type { BadgeProps } from "@/components/ui/badge";

export const MESSAGE_STATUSES = [
  { value: 0, label: "NEW" },
  { value: 1, label: "PROCESSING" },
  { value: 2, label: "COMPLETED" },
  { value: 3, label: "FAILED" },
  { value: 4, label: "DEAD" },
  { value: 5, label: "AWAITING D365" },
] as const;

const STATUS_LABELS: Record<number, string> = Object.fromEntries(
  MESSAGE_STATUSES.map((s) => [s.value, s.label]),
);

export function statusLabel(status: number): string {
  return STATUS_LABELS[status] ?? `UNKNOWN (${status})`;
}

export function statusBadgeVariant(status: number): BadgeProps["variant"] {
  switch (status) {
    case 2:
      return "success";
    case 3:
    case 4:
      return "destructive";
    case 5:
      return "warning";
    case 1:
      return "default";
    default:
      return "secondary";
  }
}

export function isErrorStatus(status: number): boolean {
  return status === 3 || status === 4;
}
