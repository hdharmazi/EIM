import * as React from "react";
import { useNavigate, useParams } from "react-router-dom";
import { ArrowLeft, RefreshCw, Copy, Pencil, Save, X } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Textarea } from "@/components/ui/textarea";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { cn } from "@/lib/utils";
import { statusBadgeVariant, statusLabel, isErrorStatus } from "@/lib/status";
import { formatDateTime, prettyJson } from "@/lib/format";
import { useLogDetails, useMessage, useReprocess, useUpdatePayload } from "./api";

export function MessageDetailPage() {
  const params = useParams();
  const id = Number(params.id);
  const navigate = useNavigate();
  const { toast } = useToast();

  const { data: message, isLoading } = useMessage(id);
  const { data: logDetails } = useLogDetails(id);
  const reprocess = useReprocess(id);
  const updatePayload = useUpdatePayload(id);

  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState("");

  function startEdit() {
    setDraft(prettyJson(message?.payload));
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

  async function onReprocess() {
    if (!window.confirm(`Reprocess message #${id}?`)) return;
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
            <Button variant="warning" size="sm" onClick={onReprocess} disabled={reprocess.isPending}>
              <RefreshCw /> Reprocess
            </Button>
            <Button variant="outline" size="sm" onClick={() => navigate("/messages")}>
              <ArrowLeft /> Back
            </Button>
          </>
        }
      />

      <div className="grid gap-4">
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

        {message.messageLog && (
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
        )}

        <Card>
          <CardHeader className="flex-row items-center justify-between py-3">
            <CardTitle className="text-sm">Payload</CardTitle>
            <div className="flex gap-2">
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

        {logDetails && logDetails.length > 0 && (
          <Card>
            <CardHeader className="py-3">
              <CardTitle className="text-sm">
                Processing Steps <Badge variant="secondary">{logDetails.length}</Badge>
              </CardTitle>
            </CardHeader>
            <CardContent className="p-0">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="w-12">#</TableHead>
                    <TableHead className="w-48">Name</TableHead>
                    <TableHead>Detail</TableHead>
                    <TableHead className="w-48">Timestamp</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {logDetails.map((log, i) => {
                    const isError =
                      log.name.toLowerCase() === "error" ||
                      (log.detail?.toUpperCase().startsWith("FAILED") ?? false);
                    return (
                      <TableRow key={log.id} className={cn(isError && "bg-destructive/5")}>
                        <TableCell className="text-muted-foreground">{i + 1}</TableCell>
                        <TableCell className="font-medium">{log.name}</TableCell>
                        <TableCell className="text-xs">
                          {log.detail && (
                            <code className="whitespace-pre-wrap">{log.detail}</code>
                          )}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                          {formatDateTime(log.createdDateTime)}
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )}
      </div>
    </div>
  );
}

function Detail({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className={cn(mono && "font-mono text-xs")}>{value || "—"}</dd>
    </div>
  );
}
