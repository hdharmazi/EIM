import { useQuery } from "@tanstack/react-query";
import { apiFetch, apiUrl, authHeader, ApiError } from "@/lib/api";

export interface SimulationConfig {
  d365Queue: string;
  captureToD365Queue: string;
  captureToD365ResponseQueue: string;
  d365ToCaptureQueue: string;
  isServiceBusConfigured: boolean;
}

export type SimLevel =
  | "info"
  | "success"
  | "warning"
  | "error"
  | "payload"
  | "received"
  | "done";

export interface SimEvent {
  level: SimLevel;
  text: string;
}

export interface SalesOrderPreview {
  pBLId?: string;
  pblId?: string;
  custName?: string;
  quantity?: number;
  lineCount?: number;
}

export interface SalesOrderSheet {
  sheetName: string;
  ok: boolean;
  orderJson?: string;
  preview?: SalesOrderPreview;
  error?: { sheet?: string; cell?: string; message?: string };
}

export interface SalesOrderParseResponse {
  sheets: SalesOrderSheet[];
}

export function useSimulationConfig() {
  return useQuery({
    queryKey: ["simulation", "config"],
    queryFn: () => apiFetch<SimulationConfig>("/api/simulation/config"),
  });
}

export async function parseSalesOrder(file: File): Promise<SalesOrderParseResponse> {
  const form = new FormData();
  form.append("file", file);
  return apiFetch<SalesOrderParseResponse>("/api/simulation/salesorder/parse", {
    method: "POST",
    body: form,
  });
}

/**
 * Runs a simulation and streams SSE events. Reads the response body incrementally
 * so log lines appear as they arrive (through the YARP BFF proxy).
 */
export async function runSimulation(
  scenario: string,
  fields: Record<string, string>,
  onEvent: (evt: SimEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const response = await fetch(apiUrl("/api/simulation/run"), {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "text/event-stream",
      ...(await authHeader()),
    },
    body: JSON.stringify({ scenario, fields }),
    signal,
  });

  if (response.status === 401 || response.status === 403) {
    throw new ApiError(response.status, "Authentication required");
  }
  if (!response.ok || !response.body) {
    throw new ApiError(response.status, await response.text());
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";
    for (const line of lines) {
      if (!line.startsWith("data: ")) continue;
      try {
        onEvent(JSON.parse(line.slice(6)) as SimEvent);
      } catch {
        /* ignore malformed event */
      }
    }
  }
}
