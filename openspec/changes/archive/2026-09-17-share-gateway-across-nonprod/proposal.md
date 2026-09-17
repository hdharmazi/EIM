## Why

The gateway is deployed once per environment — `azf-d365fsc-gateway-wtc-dev`, `-test`, `-dev1` — but
nothing about it is environment-specific. It selects the D365 environment from the request URL and
resolves the profile from `ENVIRONMENTTABLE`, and every non-production environment points at the same
`eimdb`. Unlike `EIM.Customer`, whose Service Bus triggers bind to environment-specific queues, the
gateway has no per-environment binding at all.

Duplicating it has a cost that is already being paid. Azure Functions keys do not cross applications
(verified 2026-09-16: the dev gateway's key returns 401 against the test gateway), and
`GATEWAYCONSUMER.KEYHASH` holds one hash per consumer. So one consumer cannot use two gateway apps,
which is why `eng-apps` and `eng-apps-dev` exist as duplicate rows holding two keys for what is
logically one consumer. The duplication is undocumented and looks like two consumers.

Adding an environment currently means provisioning another gateway app and another storage account
that exist only to be identical to the others.

## What Changes

- **BREAKING (deployment):** `gateway` becomes a component shared across non-production environments
  rather than deployed per environment, matching `api` and `frontend`. Requesting it against an
  environment that does not host it is refused.
- The shared gateway is named for what it is — `-nonprod` — rather than inheriting an environment
  suffix. The dashboard's `-dev` naming is a known regret and is not repeated here.
- Per-environment Service Bus queue provisioning stops being duplicated in hand-maintained Bicep
  templates that hard-code environment prefixes. `Script/Deploy-Eim.ps1 -Component queues` already
  derives queue names from the environment prefix and is the single path.
- Adding an environment becomes configuration plus per-environment resources only: queues, storage and
  a Function App. No new gateway, no new Bicep file.

Not in scope: deleting the existing per-environment gateway apps, and migrating consumer keys onto the
shared app. Both are operational steps recorded in the migration plan rather than automated here.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `deployment-automation`: the gateway moves from the per-environment set to the shared set, gains a
  hosting flag, and queue provisioning is stated to have one path rather than a script and a parallel
  set of Bicep templates.

## Impact

- `Script/Deploy-Eim.ps1` — gateway name derivation, shared-component gating, configuration lookup.
- `Script/config/deploy.*.json` — a gateway hosting flag; the gateway block moves to the hosting
  environment.
- `Script/Bicep/` — `main.bicep`, `main-addqueue.bicep` and `main-addqueueTest.bicep` hard-code `bat`
  and `test` queue names and duplicate what the `queues` component does. They are superseded.
- `Script/Docs/deployment.md` — the gateway is absent from the component table and the configuration
  table today, so the document currently claims `all` runs every component while omitting one.
- `docs/azure-deployment-pipelines-setup.md` — states which components are shared and which are
  per-environment, and omits the gateway.
- `add-gateway-key-verification` — its multi-gateway discovery design exists to manage several gateway
  apps. With one app it is unnecessary complexity and should be reduced.
- **Operational, not automated:** consumers currently hold keys for per-environment gateway apps. Those
  keys will not work against the shared app, so each consumer needs a key issued on the shared app and
  re-registered. Until then they keep working against the old apps, which are left running.
