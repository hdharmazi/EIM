import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export interface CurrentUser {
  name: string | null;
  roles: string[];
  dbUserValidated: boolean;
}

/** Fetches the current signed-in identity from the BFF-local /api/me endpoint. */
export function useMe() {
  return useQuery({
    queryKey: ["me"],
    queryFn: () => apiFetch<CurrentUser>("/api/me"),
    staleTime: 5 * 60_000,
  });
}

export function useIsAdmin(): boolean {
  const { data } = useMe();
  return data?.roles.includes("Admin") ?? false;
}
