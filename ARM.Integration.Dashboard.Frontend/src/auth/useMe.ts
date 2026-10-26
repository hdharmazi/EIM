import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

/** What this deployment offers. Optional: an older API sends none, which means every feature is off. */
export interface Features {
  simulation?: boolean;
}

export interface CurrentUser {
  name: string | null;
  roles: string[];
  dbUserValidated: boolean;
  features?: Features;
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

/** True only when the API reports the feature as on; missing (or still loading) means off. */
export function useFeature(name: keyof Features): boolean {
  const { data } = useMe();
  return data?.features?.[name] === true;
}
