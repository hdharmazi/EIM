import { useLocation, useNavigate, useParams } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { statusBadgeVariant, statusLabel } from "@/lib/status";
import { useLogDetails, useMessage } from "./api";
import {
  MessageDetailsCard,
  MessageLogCard,
  MessagePayloadCard,
  ProcessingStepsCard,
  ReprocessMessageButton,
} from "./MessageDetailSections";

export function MessageDetailPage() {
  const params = useParams();
  const id = Number(params.id);
  const navigate = useNavigate();
  const location = useLocation();
  const backTo = (location.state as { from?: string } | null)?.from ?? "/messages";
  const { data: message, isLoading } = useMessage(id);
  const { data: logDetails } = useLogDetails(id);

  if (isLoading) return <div className="text-muted-foreground">Loading…</div>;
  if (!message) return <div className="text-muted-foreground">Message not found.</div>;

  return (
    <div>
      <PageHeader
        title={
          <span className="flex items-center gap-3">
            Message #{message.id}
            <Badge variant={statusBadgeVariant(message.status)}>{statusLabel(message.status)}</Badge>
          </span>
        }
        actions={
          <>
            <ReprocessMessageButton messageId={message.id} />
            <Button variant="outline" size="sm" onClick={() => navigate(backTo)}>
              <ArrowLeft /> Back
            </Button>
          </>
        }
      />

      <div className="grid gap-4">
        <MessageDetailsCard message={message} />

        <MessageLogCard message={message} />

        <MessagePayloadCard message={message} />

        <ProcessingStepsCard logDetails={logDetails} />
      </div>
    </div>
  );
}
