import { useLocation, useNavigate } from "react-router-dom";
import { ExternalLink } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { statusBadgeVariant, statusLabel } from "@/lib/status";
import { useLogDetails, useMessage } from "./api";
import {
  MessageDetailsCard,
  MessageLogCard,
  MessagePayloadCard,
  ProcessingStepsCard,
  ReprocessMessageButton,
} from "./MessageDetailSections";

export interface MessageLogDrawerProps {
  messageId: number | null;
  onOpenChange: (open: boolean) => void;
}

/** Read-only preview of a message's log/error log in a slide-in drawer, so
 * it can be viewed from the list without navigating away (and losing the
 * list's filter/search state). */
export function MessageLogDrawer({ messageId, onOpenChange }: MessageLogDrawerProps) {
  const navigate = useNavigate();
  const location = useLocation();
  const id = messageId ?? Number.NaN;

  const { data: message, isLoading, isError: isMessageError } = useMessage(id);
  const {
    data: logDetails,
    isLoading: areLogDetailsLoading,
    isError: isLogDetailsError,
  } = useLogDetails(id);

  return (
    <Sheet open={messageId !== null} onOpenChange={onOpenChange}>
      <SheetContent className="w-full max-w-2xl sm:max-w-3xl">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-3">
            {message ? (
              <>
                Message #{message.id}
                <Badge variant={statusBadgeVariant(message.status)}>
                  {statusLabel(message.status)}
                </Badge>
              </>
            ) : (
              "Message"
            )}
          </SheetTitle>
          {message && (
            <div className="flex flex-wrap gap-2">
              <ReprocessMessageButton messageId={message.id} />
              <Button
                variant="outline"
                size="sm"
                onClick={() =>
                  navigate(`/messages/${message.id}`, {
                    state: { from: `${location.pathname}${location.search}` },
                  })
                }
              >
                <ExternalLink /> Open full details
              </Button>
            </div>
          )}
        </SheetHeader>

        {isLoading ? (
          <div className="text-muted-foreground">Loading…</div>
        ) : isMessageError || !message ? (
          <div className="rounded-md border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive">
            Unable to load this message.
          </div>
        ) : (
          <div className="grid gap-4">
            <MessageDetailsCard message={message} />
            <MessageLogCard message={message} />
            <MessagePayloadCard message={message} />
            {areLogDetailsLoading ? (
              <div className="rounded-md border p-4 text-sm text-muted-foreground">
                Loading processing steps…
              </div>
            ) : isLogDetailsError ? (
              <div className="rounded-md border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive">
                Unable to load processing steps. Open full details to try again.
              </div>
            ) : (
              <ProcessingStepsCard logDetails={logDetails} />
            )}
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}
