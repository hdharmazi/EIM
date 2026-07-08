import {
  PublicClientApplication,
  type Configuration,
  type AccountInfo,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";

const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID;
const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID;

/** Scope(s) requested for the Dashboard API access token (e.g. api://<api-id>/access_as_user). */
export const apiScopes: string[] = [import.meta.env.VITE_API_SCOPE].filter(Boolean);

const msalConfig: Configuration = {
  auth: {
    clientId,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: "sessionStorage",
    storeAuthStateInCookie: false,
  },
};

export const msalInstance = new PublicClientApplication(msalConfig);

export function getActiveAccount(): AccountInfo | null {
  return msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0] ?? null;
}

/**
 * Acquires an API access token silently, falling back to an interactive redirect
 * when consent/interaction is required. Returns null while a redirect is in flight.
 */
export async function acquireApiToken(): Promise<string | null> {
  const account = getActiveAccount();
  if (!account) {
    await msalInstance.loginRedirect({ scopes: apiScopes });
    return null;
  }
  try {
    const result = await msalInstance.acquireTokenSilent({ scopes: apiScopes, account });
    return result.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect({ scopes: apiScopes, account });
      return null;
    }
    throw error;
  }
}

export function logout(): void {
  const account = getActiveAccount();
  void msalInstance.logoutRedirect({ account: account ?? undefined });
}
