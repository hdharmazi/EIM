import * as React from "react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { ArrowLeft, RefreshCw, Copy, Pencil, Save, X } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { statusBadgeVariant, statusLabel } from "@/lib/status";
import { prettyJson } from "@/lib/format";
import { useLogDetails, useMessage, useReprocess, useUpdatePayload } from "./api";
import { MessageDetailsCard, MessageLogCard, ProcessingStepsCard } from "./MessageDetailSections";

export function MessageDetailPage() {
  const params = useParams();
  const id = Number(params.id);
  const navigate = useNavigate();
  const location = useLocation();
  const backTo = (location.state as { from?: string } | null)?.from ?? "/messages";
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
            <Button variant="outline" size="sm" onClick={() => navigate(backTo)}>
              <ArrowLeft /> Back
            </Button>
          </>
        }
      />

      <div className="grid gap-4">
        <MessageDetailsCard message={message} />

        <MessageLogCard message={message} />

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

        <ProcessingStepsCard logDetails={logDetails} />
      </div>
    </div>
  );
}
