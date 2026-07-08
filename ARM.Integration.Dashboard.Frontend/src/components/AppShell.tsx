import * as React from "react";
import { NavLink, Outlet } from "react-router-dom";
import {
  LayoutDashboard,
  Inbox,
  Trash2,
  FlaskConical,
  Settings2,
  Users,
  Menu,
  LogOut,
  Zap,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { useMe, useIsAdmin } from "@/auth/useMe";
import { logout } from "@/auth/msal";

interface NavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  end?: boolean;
  adminOnly?: boolean;
}

interface NavSection {
  title: string;
  items: NavItem[];
}

const SECTIONS: NavSection[] = [
  {
    title: "Overview",
    items: [{ to: "/", label: "Dashboard", icon: LayoutDashboard, end: true }],
  },
  {
    title: "Messages",
    items: [{ to: "/messages", label: "Message Queue", icon: Inbox }],
  },
  {
    title: "Utilities",
    items: [
      { to: "/cleanup", label: "Cleanup Queue", icon: Trash2 },
      { to: "/simulation", label: "Simulation", icon: FlaskConical },
    ],
  },
  {
    title: "Configuration",
    items: [{ to: "/environments", label: "Environments", icon: Settings2 }],
  },
  {
    title: "Administration",
    items: [{ to: "/users", label: "Users", icon: Users, adminOnly: true }],
  },
];

export function AppShell() {
  const [open, setOpen] = React.useState(false);
  const { data: me } = useMe();
  const isAdmin = useIsAdmin();

  return (
    <div className="flex h-full min-h-screen bg-background">
      {/* Sidebar */}
      <aside
        className={cn(
          "fixed inset-y-0 left-0 z-40 w-64 -translate-x-full border-r bg-card transition-transform lg:translate-x-0",
          open && "translate-x-0",
        )}
      >
        <div className="flex h-14 items-center gap-2 border-b px-5 font-semibold">
          <Zap className="h-5 w-5 text-primary" />
          <span>EIM Dashboard</span>
        </div>
        <nav className="flex flex-col gap-4 overflow-y-auto p-3">
          {SECTIONS.map((section) => {
            const items = section.items.filter((i) => !i.adminOnly || isAdmin);
            if (items.length === 0) return null;
            return (
              <div key={section.title}>
                <div className="px-2 pb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                  {section.title}
                </div>
                {items.map((item) => (
                  <NavLink
                    key={item.to}
                    to={item.to}
                    end={item.end}
                    onClick={() => setOpen(false)}
                    className={({ isActive }) =>
                      cn(
                        "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors",
                        isActive
                          ? "bg-primary text-primary-foreground"
                          : "text-foreground hover:bg-accent",
                      )
                    }
                  >
                    <item.icon className="h-4 w-4" />
                    {item.label}
                  </NavLink>
                ))}
              </div>
            );
          })}
        </nav>
      </aside>

      {open && (
        <div
          className="fixed inset-0 z-30 bg-black/40 lg:hidden"
          onClick={() => setOpen(false)}
          aria-hidden
        />
      )}

      {/* Main */}
      <div className="flex min-w-0 flex-1 flex-col lg:pl-64">
        <header className="sticky top-0 z-20 flex h-14 items-center gap-3 border-b bg-card/80 px-4 backdrop-blur">
          <button
            className="rounded-md p-2 hover:bg-accent lg:hidden"
            onClick={() => setOpen((v) => !v)}
            aria-label="Toggle navigation"
          >
            <Menu className="h-5 w-5" />
          </button>
          <div className="ml-auto flex items-center gap-3">
            {me?.name && <span className="text-sm text-muted-foreground">{me.name}</span>}
            <button
              onClick={() => logout()}
              className="inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm hover:bg-accent"
            >
              <LogOut className="h-4 w-4" /> Sign out
            </button>
          </div>
        </header>

        <main className="flex-1 p-4 md:p-6">
          <Outlet />
        </main>

        <footer className="border-t px-6 py-3 text-xs text-muted-foreground">
          © {new Date().getFullYear()} — ARM Integration EIM
        </footer>
      </div>
    </div>
  );
}
