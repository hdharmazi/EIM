import * as React from "react";
import { Play, Loader2, Trash2, Upload } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { useConfigurations } from "@/features/configuration/api";
import { useIsAdmin } from "@/auth/useMe";
import { useNavigate } from "react-router-dom";
import { SCENARIOS, groupedScenarios } from "./scenarios";
import {
  parseSalesOrder,
  runSimulation,
  useSimulationConfig,
  type SalesOrderSheet,
  type SimEvent,
  type SimLevel,
} from "./api";

const LEVEL_COLORS: Record<SimLevel, string> = {
  info: "#9cdcfe",
  success: "#4ec94e",
  warning: "#dcdcaa",
  error: "#f44747",
  payload: "#ce9178",
  received: "#4fc1ff",
  done: "#6a9955",
};

const LEVEL_PREFIX: Record<SimLevel, string> = {
  info: "  ",
  success: "\u2713 ",
  warning: "\u26a0 ",
  error: "\u2717 ",
  payload: "  \uD83D\uDCE4 ",
  received: "  \uD83D\uDCE5 ",
  done: "  ",
};

function formatLine(evt: SimEvent): string {
  const prefix = LEVEL_PREFIX[evt.level] ?? "  ";
  if (evt.level === "payload" || evt.level === "received") {
    try {
      return prefix + JSON.stringify(JSON.parse(evt.text), null, 2);
    } catch {
      return prefix + evt.text;
    }
  }
  return prefix + evt.text;
}

export function SimulationPage() {
  const { data: config } = useSimulationConfig();
  const { data: environments } = useConfigurations("SimulationEnvironment");
  const isAdmin = useIsAdmin();
  const navigate = useNavigate();
  const [environment, setEnvironment] = React.useState("");
  const [scenarioId, setScenarioId] = React.useState("");
  const [fields, setFields] = React.useState<Record<string, string>>({});
  const [events, setEvents] = React.useState<SimEvent[]>([]);
  const [running, setRunning] = React.useState(false);
  const logRef = React.useRef<HTMLDivElement>(null);

  // Sales order import state
  const [dataAreaId, setDataAreaId] = React.useState("WTC");
  const [sheets, setSheets] = React.useState<SalesOrderSheet[]>([]);
  const [selectedSheet, setSelectedSheet] = React.useState<string | null>(null);
  const [parseError, setParseError] = React.useState<string | null>(null);
  const [parsing, setParsing] = React.useState(false);

  const scenario = scenarioId ? SCENARIOS[scenarioId] : undefined;
  const isImport = scenario?.custom === "salesorder-import";
  const groups = React.useMemo(() => groupedScenarios(), []);
  const selectedEnv = environments?.find((e) => e.name === environment);
  const hasEnvironments = (environments?.length ?? 0) > 0;

  React.useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight });
  }, [events]);

  function onSelectScenario(id: string) {
    setScenarioId(id);
    const next = SCENARIOS[id];
    setSelectedSheet(null);
    setSheets([]);
    setParseError(null);
    if (next && next.custom !== "salesorder-import") {
      setFields(Object.fromEntries(next.fields.map((f) => [f.k, f.d])));
    } else {
      setFields({});
    }
  }

  async function onFileChange(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    setSheets([]);
    setSelectedSheet(null);
    setParseError(null);
    if (!file) return;
    setParsing(true);
    try {
      const result = await parseSalesOrder(file);
      setSheets(result.sheets ?? []);
      if ((result.sheets ?? []).length === 0) setParseError("No worksheets found.");
    } catch (err) {
      setParseError(err instanceof Error ? err.message : "Upload failed");
    } finally {
      setParsing(false);
    }
  }

  const canRun = React.useMemo(() => {
    if (running || !scenario || !environment) return false;
    if (isImport) return !!selectedSheet;
    return true;
  }, [running, scenario, environment, isImport, selectedSheet]);

  async function onRun() {
    if (!scenario) return;
    let payload: Record<string, string>;
    if (isImport) {
      const sheet = sheets.find((s) => s.sheetName === selectedSheet && s.ok);
      if (!sheet?.orderJson) {
        setEvents((prev) => [...prev, { level: "error", text: "Select a parsed sheet before running." }]);
        return;
      }
      payload = { order: sheet.orderJson, dataAreaId: dataAreaId || "WTC" };
    } else {
      payload = fields;
    }

    setEvents([
      { level: "info", text: `Starting: ${scenario.label}` },
      { level: "info", text: new Date().toISOString() },
    ]);
    setRunning(true);
    try {
      await runSimulation(scenarioId, payload, environment, (evt) => {
        setEvents((prev) => [...prev, evt]);
      });
    } catch (err) {
      setEvents((prev) => [
        ...prev,
        { level: "error", text: `Connection error: ${err instanceof Error ? err.message : String(err)}` },
      ]);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <PageHeader
        title="Integration Simulation"
        description="Send test messages to Service Bus and watch results in real time"
      />

      <Card className="mb-4">
        <CardHeader className="flex-row items-center justify-between py-3">
          <CardTitle className="text-sm">Simulation Environment</CardTitle>
          {config?.isServiceBusConfigured ? (
            <Badge variant="success">Service Bus: Configured</Badge>
          ) : (
            <Badge variant="warning">Service Bus: Not configured</Badge>
          )}
        </CardHeader>
        <CardContent className="space-y-4">
          {!hasEnvironments ? (
            <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
              No simulation environments are configured yet.{" "}
              {isAdmin ? (
                <Button
                  variant="link"
                  className="h-auto p-0 text-sm"
                  onClick={() => navigate("/configuration/new?category=SimulationEnvironment")}
                >
                  Create one
                </Button>
              ) : (
                "Ask an administrator to create one"
              )}{" "}
              before running a simulation.
            </div>
          ) : (
            <>
              <div className="max-w-sm space-y-2">
                <Label>Environment</Label>
                <Select value={environment} onValueChange={setEnvironment}>
                  <SelectTrigger>
                    <SelectValue placeholder="— select an environment —" />
                  </SelectTrigger>
                  <SelectContent>
                    {environments!.map((e) => (
                      <SelectItem key={e.name} value={e.name}>
                        {e.displayName ? `${e.name} — ${e.displayName}` : e.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              {selectedEnv && (
                <div className="grid grid-cols-1 gap-3 text-sm md:grid-cols-2">
                  {(
                    [
                      "D365FscDataEventQueue",
                      "CaptureToD365Queue",
                      "CaptureToD365ResponseQueue",
                      "D365ToCaptureQueue",
                    ] as const
                  ).map((key) => (
                    <div key={key}>
                      <div className="text-muted-foreground">{key}</div>
                      <div className="font-medium">{selectedEnv.fields[key] || "Not configured"}</div>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-12">
        {/* Left: scenario + fields */}
        <Card className="lg:col-span-5">
          <CardContent className="pt-6">
            <div className="mb-4 space-y-2">
              <Label>Scenario</Label>
              <Select value={scenarioId} onValueChange={onSelectScenario}>
                <SelectTrigger>
                  <SelectValue placeholder="— select a scenario —" />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(groups).map(([groupName, items]) => (
                    <div key={groupName}>
                      <div className="px-2 py-1.5 text-xs font-semibold text-muted-foreground">
                        {groupName}
                      </div>
                      {items.map((item) => (
                        <SelectItem key={item.id} value={item.id}>
                          {item.label}
                        </SelectItem>
                      ))}
                    </div>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {scenario && !isImport && (
              <div className="space-y-3">
                <div className="text-xs font-semibold text-muted-foreground">Payload Fields</div>
                {scenario.fields.map((f) => (
                  <div key={f.k} className="space-y-1">
                    <Label className="text-xs">
                      {f.l}
                      {f.req && <span className="text-destructive"> *</span>}
                    </Label>
                    <Input
                      value={fields[f.k] ?? ""}
                      onChange={(e) => setFields((prev) => ({ ...prev, [f.k]: e.target.value }))}
                    />
                  </div>
                ))}
              </div>
            )}

            {isImport && (
              <div className="space-y-3">
                <div className="text-xs font-semibold text-muted-foreground">Sales Order Import</div>
                <div className="space-y-1">
                  <Label className="text-xs">Data Area ID</Label>
                  <Input value={dataAreaId} onChange={(e) => setDataAreaId(e.target.value)} />
                </div>
                <div className="space-y-1">
                  <Label className="text-xs">Workbook (.xlsx)</Label>
                  <Input type="file" accept=".xlsx" onChange={onFileChange} />
                </div>
                {parsing && <div className="text-xs text-muted-foreground">Parsing workbook…</div>}
                {parseError && <div className="text-xs text-destructive">{parseError}</div>}
                {sheets.length > 0 && (
                  <div className="space-y-1">
                    <div className="text-xs font-semibold text-muted-foreground">
                      Select a sheet (one order)
                    </div>
                    {sheets.map((s) =>
                      s.ok ? (
                        <label
                          key={s.sheetName}
                          className="flex cursor-pointer items-start gap-2 text-xs"
                        >
                          <input
                            type="radio"
                            name="soSheet"
                            className="mt-0.5"
                            checked={selectedSheet === s.sheetName}
                            onChange={() => setSelectedSheet(s.sheetName)}
                          />
                          <span>
                            <span className="font-semibold">{s.sheetName}</span> —{" "}
                            {s.preview?.pBLId ?? s.preview?.pblId ?? ""} · {s.preview?.custName ?? ""} · Qty{" "}
                            {s.preview?.quantity ?? 0} · {s.preview?.lineCount ?? 0} lines
                          </span>
                        </label>
                      ) : (
                        <div key={s.sheetName} className="py-1 text-xs text-destructive">
                          <span className="font-semibold">{s.sheetName}</span> —{" "}
                          {s.error?.cell ? `[${s.error.cell}] ` : ""}
                          {s.error?.message ?? "invalid"}
                        </div>
                      ),
                    )}
                  </div>
                )}
              </div>
            )}

            <Button className="mt-4 w-full" disabled={!canRun} onClick={onRun}>
              {running ? (
                <>
                  <Loader2 className="animate-spin" /> Running…
                </>
              ) : isImport ? (
                <>
                  <Upload /> Run Import
                </>
              ) : (
                <>
                  <Play /> Run Simulation
                </>
              )}
            </Button>
          </CardContent>
        </Card>

        {/* Right: live log */}
        <Card className="lg:col-span-7">
          <CardHeader className="flex-row items-center justify-between py-3">
            <CardTitle className="text-sm">Output</CardTitle>
            <Button variant="outline" size="sm" onClick={() => setEvents([])}>
              <Trash2 /> Clear
            </Button>
          </CardHeader>
          <CardContent>
            <div
              ref={logRef}
              className="max-h-[600px] min-h-[420px] overflow-y-auto rounded-md p-3 font-mono text-[0.82rem]"
              style={{ background: "#1e1e1e" }}
            >
              {events.length === 0 ? (
                <span style={{ color: "#6a9955" }}>
                  // Select a scenario and click Run Simulation
                </span>
              ) : (
                events.map((evt, i) => (
                  <div
                    key={i}
                    className={cn("mb-0.5 whitespace-pre-wrap break-all")}
                    style={{ color: LEVEL_COLORS[evt.level] ?? "#d4d4d4" }}
                  >
                    {formatLine(evt)}
                  </div>
                ))
              )}
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
