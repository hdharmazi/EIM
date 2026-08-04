import * as React from "react";
import { useSearchParams } from "react-router-dom";
import { Search, Eye } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { MESSAGE_STATUSES, isErrorStatus, statusBadgeVariant, statusLabel } from "@/lib/status";
import { formatDateTime } from "@/lib/format";
import { useMessages } from "./api";
import { MessageLogDrawer } from "./MessageLogDrawer";

const ALL = "all";

export function MessageListPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [selectedId, setSelectedId] = React.useState<number | null>(null);

  const statusParam = searchParams.get("status");
  const status = statusParam !== null ? Number(statusParam) : undefined;
  const activeSearch = searchParams.get("search") ?? "";
  const [search, setSearch] = React.useState(activeSearch);

  const { data, isLoading } = useMessages({ status, search: activeSearch });

  function onStatusChange(value: string) {
    const next = new URLSearchParams(searchParams);
    if (value === ALL) next.delete("status");
    else next.set("status", value);
    setSearchParams(next);
  }

  function applySearch(e: React.FormEvent) {
    e.preventDefault();
    const next = new URLSearchParams(searchParams);
    if (search) next.set("search", search);
    else next.delete("search");
    setSearchParams(next);
  }

  function clearFilters() {
    setSearch("");
    setSearchParams({});
  }

  return (
    <div>
      <PageHeader
        title="Message Queue"
        description={data ? `Showing ${data.length} record(s)` : undefined}
      />

      <form onSubmit={applySearch} className="mb-4 flex flex-wrap items-center gap-2">
        <div className="w-48">
          <Select value={statusParam ?? ALL} onValueChange={onStatusChange}>
            <SelectTrigger>
              <SelectValue placeholder="All Statuses" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>All Statuses</SelectItem>
              {MESSAGE_STATUSES.map((s) => (
                <SelectItem key={s.value} value={String(s.value)}>
                  {s.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="relative min-w-[240px] flex-1">
          <Search className="absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            className="pl-8"
            placeholder="Search source ID, type, system…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <Button type="submit">Filter</Button>
        <Button type="button" variant="outline" onClick={clearFilters}>
          Clear
        </Button>
      </form>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>ID</TableHead>
              <TableHead>Type</TableHead>
              <TableHead>Source</TableHead>
              <TableHead>Destination</TableHead>
              <TableHead>Source ID</TableHead>
              <TableHead>Environment</TableHead>
              <TableHead>Status</TableHead>
              <TableHead>Modified</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {isLoading ? (
              <TableRow>
                <TableCell colSpan={9} className="py-8 text-center text-muted-foreground">
                  Loading…
                </TableCell>
              </TableRow>
            ) : !data || data.length === 0 ? (
              <TableRow>
                <TableCell colSpan={9} className="py-8 text-center text-muted-foreground">
                  No messages found.
                </TableCell>
              </TableRow>
            ) : (
              data.map((m) => (
                <TableRow
                  key={m.id}
                  className={cn(isErrorStatus(m.status) && "bg-destructive/5")}
                >
                  <TableCell className="font-medium">{m.id}</TableCell>
                  <TableCell className="max-w-[220px] truncate text-xs" title={m.type}>
                    {m.type}
                  </TableCell>
                  <TableCell className="text-xs">{m.sourceSystem}</TableCell>
                  <TableCell className="text-xs">{m.destinationSystem}</TableCell>
                  <TableCell className="text-xs text-muted-foreground">{m.sourceId}</TableCell>
                  <TableCell className="text-xs">{m.environment}</TableCell>
                  <TableCell>
                    <Badge variant={statusBadgeVariant(m.status)}>{statusLabel(m.status)}</Badge>
                  </TableCell>
                  <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                    {formatDateTime(m.modifiedDateTime)}
                  </TableCell>
                  <TableCell>
                    <Button variant="outline" size="sm" onClick={() => setSelectedId(m.id)}>
                      <Eye /> View
                    </Button>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>

      <MessageLogDrawer
        messageId={selectedId}
        onOpenChange={(open) => {
          if (!open) setSelectedId(null);
        }}
      />
    </div>
  );
}
