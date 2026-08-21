import * as React from "react";
import { Copy, Pencil, RefreshCw, Save, X } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { cn } from "@/lib/utils";
import { isErrorStatus } from "@/lib/status";
import { formatDateTime, prettyJson } from "@/lib/format";
import {
  useReprocess,
  useUpdatePayload,
  type MessageLogDetail,
  type MessageQueueRecord,
} from "./api";

/** Shared read-only sections of a message's details, used by both the full
 * detail page and the list's log-preview drawer. */

export function Detail({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className={cn(mono && "font-mono text-xs")}>{value || "—"}</dd>
    </div>
  );
}

export function MessageDetailsCard({ message }: { message: MessageQueueRecord }) {
  return (
    <Card>
      <CardHeader className="py-3">
        <CardTitle className="text-sm">Message Details</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
          <Detail label="Type" value={message.type} />
          <Detail label="Source" value={`${message.sourceSystem} / ${message.sourceEntity}`} />
          <Detail
            label="Destination"
            value={`${message.destinationSystem} / ${message.destinationEntity}`}
          />
          <Detail label="Source ID" value={message.sourceId} mono />
          <Detail label="Queue" value={message.queue} />
          <Detail label="Response Queue" value={message.responseQueue} />
          <Detail label="Environment" value={message.environment} />
          <Detail label="Created" value={formatDateTime(message.createdAt)} />
          <Detail label="Modified" value={formatDateTime(message.modifiedDateTime)} />
        </dl>
      </CardContent>
    </Card>
  );
}

export function MessageLogCard({ message }: { message: MessageQueueRecord }) {
  if (!message.messageLog) return null;
  return (
    <Card className={cn(isErrorStatus(message.status) && "border-destructive")}>
      <CardHeader className="py-3">
        <CardTitle className="text-sm">
          {isErrorStatus(message.status) ? "Error Log" : "Message Log"}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <pre className="whitespace-pre-wrap break-words rounded bg-muted p-3 text-xs">
          {prettyJson(message.messageLog)}
        </pre>
      </CardContent>
    </Card>
  );
}

export function MessagePayloadCard({ message }: { message: MessageQueueRecord }) {
  const { toast } = useToast();
  const updatePayload = useUpdatePayload(message.id);
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState("");

  function startEdit() {
    setDraft(prettyJson(message.payload));
    setEditing(true);
  }

  async function savePayload() {
    try {
      await updatePayload.mutateAsync(draft);
      setEditing(false);
      toast({ variant: "success", title: "Payload saved" });
    } catch (err) {
      toast({ variant: "destructive", title: "Save failed", description: String(err) });
    }
  }

  return (
    <Card>
      <CardHeader className="flex-row flex-wrap items-center justify-between gap-2 py-3">
        <CardTitle className="text-sm">Payload</CardTitle>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              navigator.clipboard.writeText(message.payload);
              toast({ title: "Payload copied" });
            }}
          >
            <Copy /> Copy
          </Button>
          {!editing ? (
            <Button variant="outline" size="sm" onClick={startEdit}>
              <Pencil /> Edit
            </Button>
          ) : (
            <>
              <Button
                variant="success"
                size="sm"
                onClick={savePayload}
                disabled={updatePayload.isPending}
              >
                <Save /> Save
              </Button>
              <Button variant="outline" size="sm" onClick={() => setEditing(false)}>
                <X /> Cancel
              </Button>
            </>
          )}
        </div>
      </CardHeader>
      <CardContent>
        {editing ? (
          <Textarea
            className="min-h-[320px] font-mono text-xs"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
          />
        ) : (
          <pre className="max-h-[400px] overflow-auto whitespace-pre-wrap break-words rounded bg-muted p-3 text-xs">
            {prettyJson(message.payload)}
          </pre>
        )}
      </CardContent>
    </Card>
  );
}

export function ReprocessMessageButton({ messageId }: { messageId: number }) {
  const { toast } = useToast();
  const reprocess = useReprocess(messageId);

  async function onReprocess() {
    if (!window.confirm(`Reprocess message #${messageId}?`)) return;
    try {
      const result = await reprocess.mutateAsync();
      toast({
        variant: result.success ? "success" : "destructive",
        title: result.success ? "Reprocess succeeded" : "Reprocess failed",
        description: result.message,
      });
    } catch (err) {
      toast({ variant: "destructive", title: "Reprocess failed", description: String(err) });
    }
  }

  return (
    <Button variant="warning" size="sm" onClick={onReprocess} disabled={reprocess.isPending}>
      <RefreshCw /> Reprocess
    </Button>
  );
}

export function ProcessingStepsCard({ logDetails }: { logDetails: MessageLogDetail[] | undefined }) {
  const steps = logDetails ?? [];

  return (
    <Card>
      <CardHeader className="py-3">
        <CardTitle className="text-sm">
          Processing Steps <Badge variant="secondary">{steps.length}</Badge>
        </CardTitle>
      </CardHeader>
      {steps.length === 0 ? (
        <CardContent className="text-sm text-muted-foreground">
          No processing steps recorded.
        </CardContent>
      ) : (
        <CardContent className="p-0">
          <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead className="w-10 sm:w-12">#</TableHead>
              <TableHead className="w-28 sm:w-40">Name</TableHead>
              <TableHead>Detail</TableHead>
              <TableHead className="w-32 sm:w-44">Timestamp</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {steps.map((log, i) => {
              const isError =
                log.name.toLowerCase() === "error" ||
                (log.detail?.toUpperCase().startsWith("FAILED") ?? false);
              return (
                <TableRow key={log.id} className={cn(isError && "bg-destructive/5")}>
                  <TableCell className="text-muted-foreground">{i + 1}</TableCell>
                  <TableCell className={cn("break-words font-medium", isError && "text-destructive")}>
                    {log.name}
                  </TableCell>
                  <TableCell className="min-w-0 text-xs">
                    {log.detail ? (
                      <code className="block whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
                        {log.detail}
                      </code>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </TableCell>
                  <TableCell className="break-words text-xs text-muted-foreground">
                    {formatDateTime(log.createdDateTime)}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
          </Table>
        </CardContent>
      )}
    </Card>
  );
}
