import React from "react";
import ReactDOM from "react-dom/client";
import { QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "react-router-dom";
import { MsalProvider } from "@azure/msal-react";
import { queryClient } from "@/lib/api";
import { msalInstance } from "@/auth/msal";
import { ToastProvider } from "@/components/ui/toast";
import { router } from "@/router";
import "./index.css";

async function bootstrap() {
  await msalInstance.initialize();

  // Complete any interactive redirect (login/token) that is returning to the app.
  const redirectResult = await msalInstance.handleRedirectPromise();
  if (redirectResult?.account) {
    msalInstance.setActiveAccount(redirectResult.account);
  } else if (!msalInstance.getActiveAccount()) {
    const existing = msalInstance.getAllAccounts()[0];
    if (existing) msalInstance.setActiveAccount(existing);
  }

  // No signed-in account yet → start the sign-in redirect.
  if (!msalInstance.getActiveAccount()) {
    await msalInstance.loginRedirect();
    return;
  }

  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <MsalProvider instance={msalInstance}>
        <QueryClientProvider client={queryClient}>
          <ToastProvider>
            <RouterProvider router={router} />
          </ToastProvider>
        </QueryClientProvider>
      </MsalProvider>
    </React.StrictMode>,
  );
}

void bootstrap();
