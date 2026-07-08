import { Navigate } from "react-router-dom";
import { useMe } from "@/auth/useMe";

/** Client-side gate for admin-only routes. The server still enforces authorization. */
export function RequireAdmin({ children }: { children: React.ReactNode }) {
  const { data, isLoading } = useMe();
  if (isLoading) return <div className="text-muted-foreground">Loading…</div>;
  if (!data?.roles.includes("Admin")) return <Navigate to="/" replace />;
  return <>{children}</>;
}
