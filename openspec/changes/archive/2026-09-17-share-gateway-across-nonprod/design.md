## Context

`Deploy-Eim.ps1` divides components into two kinds. Per-environment components — `queues`, `schema`,
`function` — provision resources named for the environment. Shared components — `api`, `frontend` —
are deployed once and refused unless `dashboard.hostsDashboard` is set, so nobody accidentally creates
a second copy.

The gateway was added as a per-environment component. That was wrong, but not obviously so: it is a
Function App, and the only other Function App in the system genuinely is per-environment because its
Service Bus triggers bind to environment-specific queues. The gateway has no such binding. The D365
environment arrives in the request path and is resolved against `ENVIRONMENTTABLE` at request time.

Two facts verified on 2026-09-16 make the cost concrete:

- A key issued on the dev gateway returns **401** against the test gateway. Keys do not cross apps.
- `GATEWAYCONSUMER.KEYHASH` is a single column, so a consumer row binds to exactly one app's key.

Together these forced `eng-apps` and `eng-apps-dev` into existence: one logical consumer, split in two
so it could hold two keys. Nothing in the schema or the documentation says that is what happened.

## Goals / Non-Goals

**Goals:**

- One gateway application serving every non-production environment.
- Adding an environment requires no gateway work.
- One path for provisioning queues, not a script and a parallel set of Bicep templates.
- Naming that describes what the resource is, decided before it is created rather than regretted after.

**Non-Goals:**

- Deleting the existing per-environment gateway apps. They keep serving existing consumers until those
  consumers are cut over, and deleting them is a decision with an audience.
- Automating consumer key migration. Keys cannot be derived from stored hashes, so this needs a person
  with portal access.
- Changing how the gateway resolves an environment at request time. Nothing there is wrong.

## Decisions

### The shared gateway is `azf-d365fsc-gateway-wtc-nonprod`

The dashboard is `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` despite serving every
non-production environment. That naming is already documented as a deferred regret, kept because
renaming would drag in the linked backend and the Entra redirect URIs.

There is no reason to inherit the same mistake on a resource that does not exist yet. The shared
gateway carries `-nonprod`, and the name states the scope.

### Hosting is flagged, and requesting it elsewhere is refused

`gateway.hostsGateway` mirrors `dashboard.hostsDashboard`. Deploying the gateway from an environment
that does not host it is an error, not a no-op.

A silent skip would let someone run `-Component gateway -Environment test`, see success, and conclude
the shared gateway had been updated when nothing happened. Refusing says the request was wrong.

**Alternative considered: derive hosting from `hostsDashboard`.** Rejected. It couples two independent
decisions, and the dashboard and gateway have genuinely different reasons to live where they do — the
dashboard needs the shared App Service Plan, the gateway is on Flex Consumption and needs nothing from
it.

### Bicep templates for queues are superseded, not kept as a second option

`main.bicep`, `main-addqueue.bicep` and `main-addqueueTest.bicep` hard-code queue names with `bat-` and
`test-` prefixes. `main.bicep` additionally declares a `queueNames` array it never uses, and creates a
single queue from a separate parameter — so it does not do what reading it suggests.

`Deploy-Eim.ps1 -Component queues` derives all five queue names from the environment prefix. Keeping
the Bicep files alongside it means adding an environment has two procedures, one of which is
hand-edited, and the hand-edited one silently does less than it appears to.

They are moved to a `Superseded/` folder with a note rather than deleted, because they record what was
provisioned historically and someone may need to read them.

**`main-keyvault.bicep` is left alone.** Key Vault references are designed for and deferred; that file
is aspirational rather than superseded, and removing it would discard work not yet done.

### Existing per-environment gateway apps are left running

Cutting over means issuing a key on the shared app and re-registering it. Until that happens, an
existing consumer's key only works against the app that issued it.

Deleting the old apps before consumers move would break them. Deleting them after is an operational
decision, made by someone who can confirm nobody is still calling them.

## Risks / Trade-offs

- **One app becomes a shared blast radius.** `maximumInstanceCount` is currently the only rate limiter,
  and it becomes a limit shared across dev, bat and test. A load test in one environment can throttle
  another, and the gateway already shares a D365 tenant with the capture pipeline. → The bound is
  configurable and should be raised deliberately when environments are consolidated onto it.
- **Cutover has a window where a consumer is broken.** Registering a new key replaces the hash, so the
  old key stops matching immediately even though Azure still accepts it. → Issue the shared app's key
  first, register it, and change the consumer's base URL in the same maintenance step.
- **Three apps continue to exist, doing the same thing.** Until cutover completes, the estate is more
  confusing than before, not less. → The migration plan is ordered so the confusing state is short, and
  the old apps are named in it so they are not forgotten.
- **`-nonprod` now disagrees with `-dev` on the dashboard.** Two naming conventions in one estate. →
  Accepted deliberately: the alternative is to propagate a known mistake for the sake of consistency
  with it.

## Migration Plan

1. Deploy the shared gateway from the hosting environment.
2. Issue a host key per consumer on the shared app.
3. Re-register each consumer's key in the dashboard and point the consumer at the shared base URL.
4. Confirm no traffic remains on the per-environment apps.
5. Delete `azf-d365fsc-gateway-wtc-dev`, `-test` and `-dev1` and their storage accounts, as a separate
   decision once step 4 holds.

Rollback before step 3 is free: the old apps are untouched and still serving.

## Open Questions

- Which environment hosts the shared gateway? `bat` hosts the dashboard and is the obvious candidate,
  but it has no `gateway` configuration block today, and the gateway needs nothing from the shared App
  Service Plan that makes `bat` the dashboard's home.
- Should `eng-apps` and `eng-apps-dev` be collapsed into one consumer once a single gateway exists? The
  reason they are separate disappears, but merging them changes an identifier that a consumer sends.
