import * as React from "react";
import { cn } from "@/lib/utils";

type ToastVariant = "default" | "success" | "destructive" | "warning";

interface Toast {
  id: number;
  title?: string;
  description?: string;
  variant: ToastVariant;
}

interface ToastContextValue {
  toast: (t: Omit<Toast, "id" | "variant"> & { variant?: ToastVariant }) => void;
}

const ToastContext = React.createContext<ToastContextValue | null>(null);

let idCounter = 0;

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = React.useState<Toast[]>([]);

  const toast = React.useCallback<ToastContextValue["toast"]>((t) => {
    const id = ++idCounter;
    setToasts((prev) => [...prev, { id, variant: "default", ...t }]);
    window.setTimeout(() => {
      setToasts((prev) => prev.filter((x) => x.id !== id));
    }, 5000);
  }, []);

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div className="fixed bottom-4 right-4 z-[100] flex w-full max-w-sm flex-col gap-2">
        {toasts.map((t) => (
          <div
            key={t.id}
            className={cn(
              "rounded-md border p-4 shadow-lg animate-in slide-in-from-bottom-2",
              t.variant === "success" && "border-success/40 bg-success/10 text-success",
              t.variant === "destructive" && "border-destructive/40 bg-destructive/10 text-destructive",
              t.variant === "warning" && "border-warning/40 bg-warning/10 text-warning",
              t.variant === "default" && "border-border bg-card text-card-foreground",
            )}
          >
            {t.title && <div className="text-sm font-semibold">{t.title}</div>}
            {t.description && <div className="text-sm opacity-90">{t.description}</div>}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const ctx = React.useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within ToastProvider");
  return ctx;
}
