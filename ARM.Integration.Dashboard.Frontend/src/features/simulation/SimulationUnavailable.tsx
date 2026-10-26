import { FlaskConical } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Card, CardContent } from "@/components/ui/card";

/**
 * Shown on /simulation when the API reports simulation as off (production, or an API without the setting).
 * The API has no simulation routes then, so a deep link gets this notice rather than a broken console.
 */
export function SimulationUnavailable() {
  return (
    <div>
      <PageHeader title="Simulation" description="Publish test messages to the integration queues" />
      <Card>
        <CardContent className="flex items-center gap-3 pt-6">
          <FlaskConical className="h-8 w-8 text-muted-foreground" />
          <div className="text-sm text-muted-foreground">Simulation is not available in this environment.</div>
        </CardContent>
      </Card>
    </div>
  );
}
