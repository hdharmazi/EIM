import * as React from "react";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

interface Field {
  key: string;
  value: string;
}

interface Props {
  value: string;
  onChange: (value: string) => void;
}

/** Parses text into a flat JSON object of string values, or null if not a JSON object. */
function parseObject(text: string): Record<string, unknown> | null {
  try {
    const parsed = JSON.parse(text);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function toFields(obj: Record<string, unknown>): Field[] {
  return Object.entries(obj).map(([key, v]) => ({
    key,
    value: typeof v === "string" ? v : JSON.stringify(v),
  }));
}

/** Serializes fields back to pretty JSON, preserving typed values (number/bool/null/object) where possible. */
function fieldsToJson(fields: Field[]): string {
  const obj: Record<string, unknown> = {};
  for (const { key, value } of fields) {
    if (!key.trim()) continue;
    try {
      const parsed = JSON.parse(value);
      obj[key] = typeof parsed === "object" || typeof parsed === "number" || typeof parsed === "boolean"
        ? parsed
        : value;
    } catch {
      obj[key] = value;
    }
  }
  return JSON.stringify(obj, null, 2);
}

/**
 * Value editor with two modes: a Name/Value table for JSON objects, and a raw
 * textarea. Mirrors the legacy Environment edit view's JSON-to-fields behavior.
 */
export function JsonValueEditor({ value, onChange }: Props) {
  const initialObj = parseObject(value);
  const [mode, setMode] = React.useState<"fields" | "raw">(initialObj ? "fields" : "raw");
  const [fields, setFields] = React.useState<Field[]>(initialObj ? toFields(initialObj) : []);
  // Track the last value we emitted so external resets (e.g. loading a record) re-sync the table.
  const lastEmitted = React.useRef(value);

  React.useEffect(() => {
    if (value === lastEmitted.current) return;
    lastEmitted.current = value;
    const obj = parseObject(value);
    if (obj) {
      setFields(toFields(obj));
      setMode("fields");
    } else {
      setMode("raw");
    }
  }, [value]);

  function emit(next: Field[]) {
    setFields(next);
    const json = fieldsToJson(next);
    lastEmitted.current = json;
    onChange(json);
  }

  function updateField(index: number, patch: Partial<Field>) {
    emit(fields.map((f, i) => (i === index ? { ...f, ...patch } : f)));
  }

  function addField() {
    emit([...fields, { key: "", value: "" }]);
  }

  function removeField(index: number) {
    emit(fields.filter((_, i) => i !== index));
  }

  const canUseFields = parseObject(value) !== null || mode === "fields";

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <p className="text-xs text-muted-foreground">
          {mode === "fields"
            ? "JSON fields detected — edit individual values below"
            : "Raw value"}
        </p>
        {mode === "fields" ? (
          <Button type="button" variant="outline" size="sm" onClick={() => setMode("raw")}>
            Switch to raw
          </Button>
        ) : (
          canUseFields && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => {
                const obj = parseObject(value);
                if (obj) setFields(toFields(obj));
                setMode("fields");
              }}
            >
              Switch to fields
            </Button>
          )
        )}
      </div>

      {mode === "fields" ? (
        <div className="space-y-2">
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Value</TableHead>
                  <TableHead className="w-10" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {fields.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={3} className="py-4 text-center text-muted-foreground">
                      No fields. Add one below.
                    </TableCell>
                  </TableRow>
                ) : (
                  fields.map((f, i) => (
                    <TableRow key={i}>
                      <TableCell className="align-top">
                        <Input
                          value={f.key}
                          onChange={(e) => updateField(i, { key: e.target.value })}
                          placeholder="Name"
                        />
                      </TableCell>
                      <TableCell className="align-top">
                        <Input
                          value={f.value}
                          onChange={(e) => updateField(i, { value: e.target.value })}
                          placeholder="Value"
                        />
                      </TableCell>
                      <TableCell className="align-top">
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="text-destructive"
                          onClick={() => removeField(i)}
                        >
                          <X />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </div>
          <Button type="button" variant="outline" size="sm" onClick={addField}>
            <Plus /> Add field
          </Button>
        </div>
      ) : (
        <Textarea
          className="min-h-[120px] font-mono text-xs"
          value={value}
          onChange={(e) => {
            lastEmitted.current = e.target.value;
            onChange(e.target.value);
          }}
        />
      )}
    </div>
  );
}
