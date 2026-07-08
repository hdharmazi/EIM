import { useNavigate } from "react-router-dom";
import { RefreshCw, AlertTriangle, ServerCog, Settings2, Users } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { MESSAGE_STATUSES, statusBadgeVariant, statusLabel } from "@/lib/status";
import { formatDateTime } from "@/lib/format";
import { useRecentFailed, useStatusCounts } from "@/features/messagequeue/api";
import { useSimulationConfig } from "@/features/simulation/api";
import { useEnvironments } from "@/features/environment/api";
import { useUsers } from "@/features/users/api";

export function DashboardPage() {
  const navigate = useNavigate();
  const counts = useStatusCounts();
  const recentFailed = useRecentFailed(8);
  const config = useSimulationConfig();
  const environments = useEnvironments({});
  const users = useUsers();

  function refreshAll() {
    counts.refetch();
    recentFailed.refetch();
    config.refetch();
    environments.refetch();
    users.refetch();
  }

  return (
    <div>
      <PageHeader
        title="Dashboard"
        description="Operational overview of the integration platform"
        actions={
          <Button variant="outline" size="sm" onClick={refreshAll}>
            <RefreshCw /> Refresh
          </Button>
        }
      />

      {/* Status cards */}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
        {MESSAGE_STATUSES.map((s) => {
          const count = counts.data?.[String(s.value)] ?? 0;
          return (
            <button
              key={s.value}
              onClick={() => navigate(`/messages?status=${s.value}`)}
              className="text-left"
            >
              <Card className="transition-colors hover:border-primary">
                <CardContent className="pt-6">
                  <div className="text-2xl font-semibold">{count}</div>
                  <Badge variant={statusBadgeVariant(s.value)} className="mt-2">
                    {s.label}
                  </Badge>
                </CardContent>
              </Card>
            </button>
          );
        })}
      </div>

      {/* Health + counts */}
      <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-3">
        <Card>
          <CardContent className="flex items-center gap-3 pt-6">
            <ServerCog className="h-8 w-8 text-muted-foreground" />
            <div>
              <div className="text-sm text-muted-foreground">Service Bus</div>
              {config.data?.isServiceBusConfigured ? (
                <Badge variant="success">Configured</Badge>
              ) : (
                <Badge variant="warning">Not configured</Badge>
              )}
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex items-center gap-3 pt-6">
            <Settings2 className="h-8 w-8 text-muted-foreground" />
            <div>
              <div className="text-2xl font-semibold">{environments.data?.length ?? 0}</div>
              <div className="text-sm text-muted-foreground">Environment records</div>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex items-center gap-3 pt-6">
            <Users className="h-8 w-8 text-muted-foreground" />
            <div>
              <div className="text-2xl font-semibold">{users.data?.length ?? 0}</div>
              <div className="text-sm text-muted-foreground">Users</div>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Recent failures */}
      <Card className="mt-4">
        <CardHeader className="flex-row items-center gap-2">
          <AlertTriangle className="h-4 w-4 text-destructive" />
          <CardTitle className="text-sm">Recent Failures</CardTitle>
        </CardHeader>
        <CardContent>
          {recentFailed.data && recentFailed.data.length > 0 ? (
            <div className="divide-y">
              {recentFailed.data.map((m) => (
                <button
                  key={m.id}
                  onClick={() => navigate(`/messages/${m.id}`)}
                  className="flex w-full items-center justify-between gap-3 py-2 text-left text-sm hover:bg-accent/50"
                >
                  <span className="flex items-center gap-2">
                    <Badge variant={statusBadgeVariant(m.status)}>{statusLabel(m.status)}</Badge>
                    <span className="font-medium">#{m.id}</span>
                    <span className="text-muted-foreground">{m.type}</span>
                  </span>
                  <span className="text-muted-foreground">{formatDateTime(m.modifiedDateTime)}</span>
                </button>
              ))}
            </div>
          ) : (
            <div className="py-4 text-center text-sm text-muted-foreground">No recent failures.</div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
