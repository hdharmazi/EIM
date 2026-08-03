import * as React from "react";
import { useForm, Controller } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { useToast } from "@/components/ui/toast";
import { JsonValueEditor } from "@/features/environment/JsonValueEditor";
import {
  useConfiguration,
  useCreateConfiguration,
  useUpdateConfiguration,
} from "./api";

const SIMULATION_ENVIRONMENT = "SimulationEnvironment";

/** Empty template of the four queue fields when creating a SimulationEnvironment config. */
const SIM_ENV_TEMPLATE = JSON.stringify(
  {
    D365Queue: "",
    CaptureToD365Queue: "",
    CaptureToD365ResponseQueue: "",
    D365ToCaptureQueue: "",
  },
  null,
  2,
);

const schema = z.object({
  category: z.string().min(1, "Category is required"),
  name: z.string().min(1, "Name is required"),
  displayName: z.string().optional(),
  isActive: z.boolean(),
  value: z.string().min(1, "At least one field is required"),
});

type FormValues = z.infer<typeof schema>;

export function ConfigurationFormPage() {
  const params = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const { toast } = useToast();

  const editCategory = params.category ? decodeURIComponent(params.category) : undefined;
  const editName = params.name ? decodeURIComponent(params.name) : undefined;
  const isEdit = !!editCategory && !!editName;

  const presetCategory = searchParams.get("category") ?? SIMULATION_ENVIRONMENT;

  const existing = useConfiguration(editCategory ?? "", editName ?? "", isEdit);
  const create = useCreateConfiguration();
  const update = useUpdateConfiguration();

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      category: presetCategory,
      name: "",
      displayName: "",
      isActive: true,
      value: presetCategory === SIMULATION_ENVIRONMENT ? SIM_ENV_TEMPLATE : "{}",
    },
  });

  React.useEffect(() => {
    if (isEdit && existing.data) {
      form.reset({
        category: existing.data.category,
        name: existing.data.name,
        displayName: existing.data.displayName ?? "",
        isActive: existing.data.isActive,
        value: JSON.stringify(existing.data.fields ?? {}, null, 2),
      });
    }
  }, [isEdit, existing.data, form]);

  async function onSubmit(values: FormValues) {
    let fields: Record<string, string>;
    try {
      const parsed = JSON.parse(values.value);
      if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
        throw new Error("Fields must be a JSON object.");
      }
      fields = Object.fromEntries(
        Object.entries(parsed).map(([k, v]) => [k, typeof v === "string" ? v : String(v)]),
      );
    } catch (err) {
      toast({
        variant: "destructive",
        title: "Invalid fields",
        description: err instanceof Error ? err.message : "Could not parse fields.",
      });
      return;
    }

    const payload = {
      category: values.category,
      name: values.name,
      displayName: values.displayName || undefined,
      isActive: values.isActive,
      fields,
    };

    try {
      if (isEdit) await update.mutateAsync(payload);
      else await create.mutateAsync(payload);
      toast({ variant: "success", title: isEdit ? "Configuration updated" : "Configuration created" });
      navigate("/configuration");
    } catch (err) {
      toast({ variant: "destructive", title: "Save failed", description: String(err) });
    }
  }

  return (
    <div className="max-w-2xl">
      <PageHeader
        title={isEdit ? "Edit Configuration" : "New Configuration"}
        actions={
          <Button variant="outline" size="sm" onClick={() => navigate("/configuration")}>
            <ArrowLeft /> Back
          </Button>
        }
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-1">
              <Label>Category</Label>
              <Input disabled={isEdit} {...form.register("category")} />
              {form.formState.errors.category && (
                <p className="text-xs text-destructive">{form.formState.errors.category.message}</p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Name</Label>
              <Input disabled={isEdit} {...form.register("name")} />
              {form.formState.errors.name && (
                <p className="text-xs text-destructive">{form.formState.errors.name.message}</p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Display Name</Label>
              <Input {...form.register("displayName")} />
            </div>
            <div className="flex items-center gap-3">
              <Controller
                control={form.control}
                name="isActive"
                render={({ field }) => (
                  <Switch checked={field.value} onCheckedChange={field.onChange} />
                )}
              />
              <Label>Active</Label>
            </div>
            <div className="space-y-1">
              <Label>Fields</Label>
              <Controller
                control={form.control}
                name="value"
                render={({ field }) => (
                  <JsonValueEditor value={field.value} onChange={field.onChange} />
                )}
              />
              {form.formState.errors.value && (
                <p className="text-xs text-destructive">{form.formState.errors.value.message}</p>
              )}
            </div>
            <div className="flex gap-2">
              <Button type="submit" disabled={create.isPending || update.isPending}>
                {isEdit ? "Save Changes" : "Create"}
              </Button>
              <Button type="button" variant="outline" onClick={() => navigate("/configuration")}>
                Cancel
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
