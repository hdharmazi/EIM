import { createBrowserRouter } from "react-router-dom";
import { AppShell } from "@/components/AppShell";
import { RequireAdmin } from "@/auth/RequireAdmin";
import { DashboardPage } from "@/features/dashboard/DashboardPage";
import { MessageListPage } from "@/features/messagequeue/MessageListPage";
import { MessageDetailPage } from "@/features/messagequeue/MessageDetailPage";
import { CleanupPage } from "@/features/messagequeue/CleanupPage";
import { EnvironmentListPage } from "@/features/environment/EnvironmentListPage";
import { EnvironmentFormPage } from "@/features/environment/EnvironmentFormPage";
import { ConfigurationListPage } from "@/features/configuration/ConfigurationListPage";
import { ConfigurationFormPage } from "@/features/configuration/ConfigurationFormPage";
import { UserListPage } from "@/features/users/UserListPage";
import { UserFormPage } from "@/features/users/UserFormPage";
import { SimulationPage } from "@/features/simulation/SimulationPage";

export const router = createBrowserRouter([
  {
    path: "/",
    element: <AppShell />,
    children: [
      { index: true, element: <DashboardPage /> },
      { path: "messages", element: <MessageListPage /> },
      { path: "messages/:id", element: <MessageDetailPage /> },
      { path: "cleanup", element: <CleanupPage /> },
      { path: "simulation", element: <SimulationPage /> },
      {
        path: "environments",
        element: (
          <RequireAdmin>
            <EnvironmentListPage />
          </RequireAdmin>
        ),
      },
      {
        path: "environments/new",
        element: (
          <RequireAdmin>
            <EnvironmentFormPage />
          </RequireAdmin>
        ),
      },
      {
        path: "environments/:system/:environment/edit",
        element: (
          <RequireAdmin>
            <EnvironmentFormPage />
          </RequireAdmin>
        ),
      },
      {
        path: "configuration",
        element: (
          <RequireAdmin>
            <ConfigurationListPage />
          </RequireAdmin>
        ),
      },
      {
        path: "configuration/new",
        element: (
          <RequireAdmin>
            <ConfigurationFormPage />
          </RequireAdmin>
        ),
      },
      {
        path: "configuration/:category/:name/edit",
        element: (
          <RequireAdmin>
            <ConfigurationFormPage />
          </RequireAdmin>
        ),
      },
      {
        path: "users",
        element: (
          <RequireAdmin>
            <UserListPage />
          </RequireAdmin>
        ),
      },
      {
        path: "users/new",
        element: (
          <RequireAdmin>
            <UserFormPage />
          </RequireAdmin>
        ),
      },
      {
        path: "users/:email/edit",
        element: (
          <RequireAdmin>
            <UserFormPage />
          </RequireAdmin>
        ),
      },
      { path: "access-denied", element: <AccessDenied /> },
      { path: "*", element: <NotFound /> },
    ],
  },
]);

function AccessDenied() {
  return (
    <div className="py-16 text-center">
      <h2 className="text-2xl font-semibold">Access Denied</h2>
      <p className="mt-2 text-muted-foreground">
        You are not authorized to access this application. Please contact your administrator.
      </p>
    </div>
  );
}

function NotFound() {
  return (
    <div className="py-16 text-center">
      <h2 className="text-2xl font-semibold">404</h2>
      <p className="mt-2 text-muted-foreground">The page you're looking for doesn't exist.</p>
    </div>
  );
}
