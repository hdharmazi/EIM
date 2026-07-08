import * as React from "react";
import { useForm, Controller } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useNavigate, useParams } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useToast } from "@/components/ui/toast";
import { JsonValueEditor } from "./JsonValueEditor";
import {
  useCreateEnvironment,
  useEnvironment,
  useUpdateEnvironment,
} from "./api";

const schema = z.object({
  system: z.string().min(1, "System is required"),
  environment: z.string().min(1, "Environment is required"),
  value: z.string().min(1, "Value is required"),
});

type FormValues = z.infer<typeof schema>;

export function EnvironmentFormPage() {
  const params = useParams();
  const navigate = useNavigate();
  const { toast } = useToast();

  const editSystem = params.system ? decodeURIComponent(params.system) : undefined;
  const editEnvironment = params.environment ? decodeURIComponent(params.environment) : undefined;
  const isEdit = !!editSystem && !!editEnvironment;

  const existing = useEnvironment(editSystem ?? "", editEnvironment ?? "", isEdit);
  const create = useCreateEnvironment();
  const update = useUpdateEnvironment();

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { system: "", environment: "", value: "" },
  });

  React.useEffect(() => {
    if (isEdit && existing.data) form.reset(existing.data);
  }, [isEdit, existing.data, form]);

  async function onSubmit(values: FormValues) {
    try {
      if (isEdit) await update.mutateAsync(values);
      else await create.mutateAsync(values);
      toast({ variant: "success", title: isEdit ? "Record updated" : "Record created" });
      navigate("/environments");
    } catch (err) {
      toast({ variant: "destructive", title: "Save failed", description: String(err) });
    }
  }

  return (
    <div className="max-w-2xl">
      <PageHeader
        title={isEdit ? "Edit Environment Record" : "New Environment Record"}
        actions={
          <Button variant="outline" size="sm" onClick={() => navigate("/environments")}>
            <ArrowLeft /> Back
          </Button>
        }
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-1">
              <Label>System</Label>
              <Input disabled={isEdit} {...form.register("system")} />
              {form.formState.errors.system && (
                <p className="text-xs text-destructive">{form.formState.errors.system.message}</p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Environment</Label>
              <Input disabled={isEdit} {...form.register("environment")} />
              {form.formState.errors.environment && (
                <p className="text-xs text-destructive">
                  {form.formState.errors.environment.message}
                </p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Value</Label>
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
              <Button type="button" variant="outline" onClick={() => navigate("/environments")}>
                Cancel
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
