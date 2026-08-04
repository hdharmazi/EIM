import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
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
import type { MessageLogDetail, MessageQueueRecord } from "./api";

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

export function ProcessingStepsCard({ logDetails }: { logDetails: MessageLogDetail[] | undefined }) {
  if (!logDetails || logDetails.length === 0) return null;
  return (
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
                    {log.detail && <code className="whitespace-pre-wrap">{log.detail}</code>}
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
  );
}
