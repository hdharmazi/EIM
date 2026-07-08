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
import { Switch } from "@/components/ui/switch";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { USER_ROLES, useCreateUser, useUpdateUser, useUser } from "./api";

const schema = z.object({
  email: z.string().min(1, "Email is required").email("Enter a valid email"),
  displayName: z.string().min(1, "Display name is required"),
  role: z.enum(USER_ROLES),
  isActive: z.boolean(),
});

type FormValues = z.infer<typeof schema>;

export function UserFormPage() {
  const params = useParams();
  const navigate = useNavigate();
  const { toast } = useToast();

  const editEmail = params.email ? decodeURIComponent(params.email) : undefined;
  const isEdit = !!editEmail;

  const existing = useUser(editEmail ?? "", isEdit);
  const create = useCreateUser();
  const update = useUpdateUser();

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: "", displayName: "", role: "Viewer", isActive: true },
  });

  React.useEffect(() => {
    if (isEdit && existing.data) {
      form.reset({
        email: existing.data.email,
        displayName: existing.data.displayName ?? "",
        role: (existing.data.role as FormValues["role"]) ?? "Viewer",
        isActive: existing.data.isActive,
      });
    }
  }, [isEdit, existing.data, form]);

  async function onSubmit(values: FormValues) {
    try {
      const payload = { ...values, createdDateTime: null };
      if (isEdit) await update.mutateAsync(payload);
      else await create.mutateAsync(payload);
      toast({ variant: "success", title: isEdit ? "User updated" : "User created" });
      navigate("/users");
    } catch (err) {
      toast({ variant: "destructive", title: "Save failed", description: String(err) });
    }
  }

  return (
    <div className="max-w-2xl">
      <PageHeader
        title={isEdit ? "Edit User" : "Add User"}
        actions={
          <Button variant="outline" size="sm" onClick={() => navigate("/users")}>
            <ArrowLeft /> Back
          </Button>
        }
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-1">
              <Label>Email</Label>
              <Input disabled={isEdit} {...form.register("email")} />
              {form.formState.errors.email && (
                <p className="text-xs text-destructive">{form.formState.errors.email.message}</p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Display Name</Label>
              <Input {...form.register("displayName")} />
              {form.formState.errors.displayName && (
                <p className="text-xs text-destructive">
                  {form.formState.errors.displayName.message}
                </p>
              )}
            </div>
            <div className="space-y-1">
              <Label>Role</Label>
              <Controller
                control={form.control}
                name="role"
                render={({ field }) => (
                  <Select value={field.value} onValueChange={field.onChange}>
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {USER_ROLES.map((r) => (
                        <SelectItem key={r} value={r}>
                          {r}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              />
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
            <div className="flex gap-2">
              <Button type="submit" disabled={create.isPending || update.isPending}>
                {isEdit ? "Save Changes" : "Create"}
              </Button>
              <Button type="button" variant="outline" onClick={() => navigate("/users")}>
                Cancel
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
