import { QueryClient } from "@tanstack/react-query";
import { acquireApiToken, msalInstance } from "@/auth/msal";

/** Base URL for the API. Empty string = same-origin (nginx proxies /api in prod, Vite proxies in dev). */
const API_BASE = import.meta.env.VITE_API_BASE_URL ?? "";

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public body?: unknown,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export interface ApiRequestOptions extends Omit<RequestInit, "body"> {
  body?: unknown;
}

/** Resolves the full URL for an API path, honoring the configured API base URL. */
export function apiUrl(path: string): string {
  const p = path.startsWith("/") ? path : `/${path}`;
  return API_BASE ? `${API_BASE.replace(/\/$/, "")}${p}` : p;
}

/** Returns the Authorization header with a fresh MSAL access token (or throws to trigger login). */
export async function authHeader(): Promise<Record<string, string>> {
  const token = await acquireApiToken();
  if (!token) throw new ApiError(401, "Authentication required");
  return { Authorization: `Bearer ${token}` };
}

function handleAuthStatus(status: number): void {
  if (status === 401) {
    void msalInstance.loginRedirect({ scopes: [] });
    throw new ApiError(401, "Redirecting to sign-in");
  }
  if (status === 403) {
    window.location.assign("/access-denied");
    throw new ApiError(403, "Access denied");
  }
}

/**
 * Central fetch wrapper for all API calls. Attaches an MSAL bearer token and
 * handles auth challenges (401 → sign-in, 403 → access denied).
 */
export async function apiFetch<T = unknown>(
  path: string,
  options: ApiRequestOptions = {},
): Promise<T> {
  const { body, headers, ...rest } = options;
  const auth = await authHeader();

  const init: RequestInit = {
    ...rest,
    headers: {
      Accept: "application/json",
      ...auth,
      ...(body !== undefined && !(body instanceof FormData)
        ? { "Content-Type": "application/json" }
        : {}),
      ...headers,
    },
  };

  if (body !== undefined) {
    init.body = body instanceof FormData ? body : JSON.stringify(body);
  }

  const response = await fetch(apiUrl(path), init);
  handleAuthStatus(response.status);

  if (!response.ok) {
    let errorBody: unknown;
    const text = await response.text();
    try {
      errorBody = text ? JSON.parse(text) : undefined;
    } catch {
      errorBody = text;
    }
    throw new ApiError(response.status, response.statusText || "Request failed", errorBody);
  }

  if (response.status === 204) return undefined as T;

  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) {
    return (await response.json()) as T;
  }
  return (await response.text()) as unknown as T;
}

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => {
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          return false;
        }
        return failureCount < 2;
      },
      staleTime: 15_000,
      refetchOnWindowFocus: false,
    },
  },
});
