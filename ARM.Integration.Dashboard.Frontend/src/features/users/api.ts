import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api";

export interface AppUser {
  email: string;
  displayName: string | null;
  role: string;
  isActive: boolean;
  createdDateTime: string | null;
}

export const USER_ROLES = ["Admin", "Editor", "Viewer"] as const;

export function useUsers() {
  return useQuery({
    queryKey: ["users"],
    queryFn: () => apiFetch<AppUser[]>("/api/user"),
  });
}

export function useUser(email: string, enabled: boolean) {
  return useQuery({
    queryKey: ["user", email],
    queryFn: () => apiFetch<AppUser>(`/api/user/${encodeURIComponent(email)}`),
    enabled,
  });
}

export function useCreateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (user: AppUser) => apiFetch<AppUser>("/api/user", { method: "POST", body: user }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["users"] }),
  });
}

export function useUpdateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (user: AppUser) =>
      apiFetch<void>(`/api/user/${encodeURIComponent(user.email)}`, { method: "PUT", body: user }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["users"] }),
  });
}

export function useDeleteUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (email: string) =>
      apiFetch<void>(`/api/user/${encodeURIComponent(email)}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["users"] }),
  });
}
