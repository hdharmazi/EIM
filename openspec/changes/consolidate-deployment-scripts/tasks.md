## 1. Establish configuration

- [x] 1.1 Inventory every value currently embedded in the deployment scripts: resource names, queue names, Entra values, connection strings, schedules, and excluded users
- [ ] 1.2 Verify each value against the live Azure app settings rather than trusting the scripts, and record any discrepancy found
- [x] 1.3 Create `Script/config/deploy.example.json` documenting every required key with placeholder values
- [x] 1.4 Create gitignored `deploy.dev1.json`, `deploy.bat.json`, `deploy.test.json` from the verified values
- [x] 1.5 Add `Script/config/deploy.*.json` to `.gitignore` with the example file excluded from the ignore
- [ ] 1.6 Confirm no committed file retains a credential once configuration is extracted

> **Configuration files (task 1.4).** `deploy.dev1.json`, `deploy.bat.json` and `deploy.test.json`
> written from values already in the repository - the connection strings are read out of
> `AzureDeploy.AppService.ps1` rather than retyped, so they cannot drift in the copying. All three
> are ignored by `.gitignore` and `deploy.example.json` is not; confirmed with `git check-ignore`.
>
> Three judgements are baked into those files and are worth challenging:
>
> - **`bat` is marked as hosting the shared dashboard, and the choice is arbitrary.** The dashboard
>   is not really owned by any environment - `AzureDeploy.AppService.ps1` deploys the API, the SPA,
>   and the `bat` and `test` Function Apps in one pass, and the resources are suffixed `-dev`. But
>   the script needs exactly one owner or task 3.7 has nothing to enforce, so `bat` got it. Moving
>   it is a one-line edit in two files.
> - **`dev1` points at `asp-appservice-p0v3-linux-dev1`, not the shared plan.** That is current
>   reality, not the target. Task 2.5 moves it onto the shared plan and deletes the empty one; the
>   configuration should be corrected then rather than describing a state that does not exist yet.
> - **Entra is the deployed pair `446b6761` / `5d1e26b3` with scope `user_impersonation`**, taken
>   from `AzureDeploy.AppService.ps1`, not the local-development pair in `appsettings.json`. Getting
>   this backwards is what would break login.
>
> `-WhatIf` for all three derives names matching the 1.1 inventory exactly: `azf-eim-customer-wtc-`
> and `stwtceimcustomer` plus the environment, and the `{env}-` queue prefix. That is a check
> against the recorded inventory, not against live Azure - task 2.7 still wants the latter.

> **Inventory (task 1.1).** Ten scripts under `Script/Deploy-Manual/`, plus> `Script/Deploy-ZipWithPublishProfile.ps1` and four Bicep templates.
>
> | Resource | Name | Notes |
> |---|---|---|
> | Resource group | `d365fsc-integration-nonprod` (centralus) | shared by every environment |
> | SQL server / db | `azql-wtc-sandbox` / `eimdb` | shared; no script provisions it |
> | Service Bus namespace | `asb-eim-wtc-nonprod` | shared; queues are `{env}-` prefixed |
> | App Service plan | `asp-appservice-p0v3-linux-dev`, `asp-appservice-p0v3-linux-dev1` | two P0v3 plans (see task 2.5) |
> | Dashboard API | `wtc-dashboard-api-dev` | App Service path |
> | Frontend | `wtc-dashboard-frontend-dev` | Static Web App, Standard SKU |
> | Function apps | `azf-eim-customer-wtc-bat`, `-test`, `-dev1` | |
> | Function storage | `stwtceimcustomerbat`, `-test`, `-dev1` | |
> | Container registry | `wtcd365fscintegrationacrdev` | Container Apps path |
> | Container Apps env | `wtcd365fscintegrationcaedev` | hosts `dashboard-api`, `dashboard-frontend`, `eim-customer` |
> | Container Apps storage | `wtcd365fscintegrationdev` | |
> | Key Vault | `kv-d365fsc-wtc-nonprod2` | provisioned by `main-keyvault.bicep`; not used by any deploy script |
>
> **Two incompatible deployment paths exist.** App Service (`AzureDeploy.AppService.ps1`, zip
> deploy) and Container Apps (`AzureDeploy` + `AzureDeploy.Update.ps1`, ACR build). Both are
> complete and both target the same logical apps. One must be chosen; this blocks task 3.1.
>
> **`AzureDeploy.AppService.UpdateAzure copy.ps1` is byte-identical** to
> `AzureDeploy.AppService.UpdateAzure.ps1` (verified by hash). Delete it.
>
> **Git history gives no authority signal.** Every tracked deploy script was last touched in a
> single commit, `ce41f32` on 2026-08-21. Nothing has been revised since, so relative age cannot
> be used to decide which path is current.
>
> **Entra drift — this one is a live landmine.** Two app registrations are in play:
>
> | Scripts | Tenant / Client |
> |---|---|
> | `AzureDeploy`, `AzureDeploy.Update.ps1`, `AzureDeploy.AppService.ps1` | `5e8a5341…` / `317063cf…` |
> | `AzureDeploy.Frontend.ps1`, both `UpdateAzure` scripts | `446b6761…` / `5d1e26b3…` |
>
> The running configuration — `ARM.Integration.Dashboard.API/appsettings.json` and the frontend's
> `.env.local` — uses `5e8a5341…` / `317063cf…`. So the `446b6761…` pair is the outlier, not the
> newer state. Running `AzureDeploy.Frontend.ps1` as it stands would rebuild the SPA against an
> app registration the API does not validate against, and login would break. Confirm against live
> Azure app settings under task 1.2 before touching either.
>
> **Committed credentials.** `AzureDeploy` and `AzureDeploy.AppService.ps1` are gitignored, so the
> worst offenders are not in history. Three tracked files are, and their credentials are in
> `HEAD`:
>
> | File | Contains |
> |---|---|
> | `Script/Deploy-Manual/AzureDeploy.Function.EIM.Customer.dev1.ps1` | SQL password + Service Bus key |
> | `Script/Deploy-Manual/AzureDeploy.AppService.API.config.ps1` | Service Bus key |
> | `Script/Bicep/script.txt` | Service Bus connection string |
>
> Same Service Bus key already flagged for rotation. Removing them from the working tree does not
> remove them from history; rotation is the only remedy. Task 1.6 must check history, not just the
> working tree.
>
> **No deployment script touches SQL.** Every path assumes `eimdb` already exists with a current
> schema. That is the gap `add-schema-migration-runner` task 5.1 fills.

> **Entra drift — resolved 2026-08-27.** Two app registrations were in play:
>
> | Scripts | Tenant / Client |
> |---|---|
> | `AzureDeploy.Frontend.ps1`, both `UpdateAzure` scripts | `446b6761…` / `5d1e26b3…` |
> | `AzureDeploy`, `AzureDeploy.Update.ps1`, `AzureDeploy.AppService.ps1` | `5e8a5341…` / `317063cf…` |
>
> The first pair is the **deployed** registration. The second is local development only — it was
> sitting in `ARM.Integration.Dashboard.API/appsettings.json`, which is the base file shipped to
> every environment, so local values were being deployed and then silently overridden by Azure app
> settings. That is why the live configuration could not be deduced from the repository.
>
> Resolved by splitting the two apart:
> - `appsettings.Development.json` (new) holds the local `TenantId` / `ClientId` / `Audience` and
>   the `http://localhost:5173` CORS origin.
> - `appsettings.json` keeps only `AzureAd.Instance`, which nothing in Azure sets, plus empty
>   placeholders documenting the keys.
> - `AzureDeploy.AppService.ps1` now sets `AzureAd__TenantId` / `__ClientId` / `__Audience` itself
>   and derives the SPA's `VITE_*` values from the same two variables. It previously set none of
>   them and carried the local pair, so re-running it would have rebuilt the SPA against a
>   registration the API does not validate and broken login. It also used the scope
>   `access_as_user`, where both the deployed frontend and `.env.local` use `user_impersonation`.
>
> This makes `AzureDeploy.AppService.UpdateAzure.ps1` and its byte-identical copy redundant — their
> only purpose was correcting the values the full deploy got wrong.

## 2. Naming and preview

- [x] 2.1 Implement a single naming function deriving Function App, storage account, App Service Plan, and queue prefix from the environment
- [x] 2.2 Support explicit per-environment name overrides for resources that do not follow the pattern
- [x] 2.3 Treat resource group and Service Bus namespace as configuration values rather than constants, so an environment in a different location can be added later without reworking the naming function
- [x] 2.4 Treat the App Service Plan as a shared configuration value rather than a derived per-environment name, hosting the dashboard API and every environment's Function App
- [ ] 2.5 Migrate the `dev1` Function App onto the shared plan when no drain or reconciliation is in flight, then delete the emptied `asp-appservice-p0v3-linux-dev1` so it stops billing
- [x] 2.6 Implement `-WhatIf` reporting the derived names and intended actions with no mutating calls
- [ ] 2.7 Run `-WhatIf` for `dev1`, `bat`, and `test` and confirm every derived name matches an existing resource exactly

> The naming function derives `azf-eim-customer-wtc-{env}`, `stwtceimcustomer{env}` and the `{env}-`
> queue prefix, with per-resource overrides in `names` for anything that does not follow the
> pattern. Resource group, location and Service Bus namespace are configuration, not constants.
> The App Service Plan is configuration too, and deliberately not derived: it is shared.
>
> `-WhatIf` for `dev1` reproduces the inventory exactly - `azf-eim-customer-wtc-dev1`,
> `stwtceimcustomerdev1`, prefix `dev1-`. That is not yet 2.7: 2.7 wants the names checked against
> live Azure, which needs the CLI and a login.

## 3. Script skeleton

- [x] 3.1 Create `Script/Deploy-Eim.ps1` accepting `-Environment` (`dev1`, `bat`, `test`), `-Component`, and `-WhatIf`
- [x] 3.2 Load and validate the environment's configuration, failing with the specific missing key when incomplete
- [x] 3.3 Support environment variables as an override so a pipeline can run without a configuration file
- [x] 3.4 Verify Azure CLI presence, login state, and the active subscription before any action
- [x] 3.5 Enforce the component order queues, schema, api, function, frontend when multiple components are requested
- [x] 3.6 Halt on first failure, name the failed step, and exit non-zero
- [x] 3.7 Reject a shared dashboard component requested against an environment that does not host it

> Both refusal paths were exercised rather than assumed. A missing key reports
> `missing required key 'appServicePlan'`; requesting `api` against `bat` reports that `bat` does
> not host the shared dashboard. Refused rather than skipped, because a request for something the
> environment does not own means the request was wrong, and quietly ignoring it hides that.

## 4. Per-environment components

- [x] 4.1 Implement the `queues` component by invoking the existing `Deploy-ServiceBusQueue-AzCli.ps1`
- [x] 4.2 Implement the `function` component: plan, storage account, Function App, app settings, publish, zip, deploy, restart — each existence-checked, following `AzureDeploy.Function.EIM.Customer.dev1.ps1`
- [x] 4.3 Add the `schema` component as a no-op placeholder that reports it was skipped until the migration runner exists
- [ ] 4.4 Deploy `function` to `dev1` and compare the result against the output of the existing dev1 script
- [ ] 4.5 Deploy `queues` to `dev1` and confirm no queue is recreated

> 4.3 is implemented rather than stubbed: the migration runner now exists, so `schema` invokes it,
> passing the connection string by environment variable so it never reaches a command line.
>
> Two behaviours carried over deliberately from the dev1 script. App settings go through a file
> rather than the command line, because connection strings contain characters the shell mangles.
> And every setting is checked non-empty before it is applied - a trigger whose schedule setting is
> missing fails to bind and takes the entire host down, which is a silent failure worth converting
> into a loud one.
>
> `api` and `frontend` throw a message naming the old script. They are section 5, and a component
> that quietly did nothing would look like success.

## 5. Shared components

- [x] 5.1 Implement the `api` component: web app existence check, app settings, publish, zip, deploy
- [x] 5.2 Implement the `frontend` component: npm build with Entra build arguments, Static Web App existence check, backend link, deploy
- [x] 5.3 Apply one Entra tenant, client, and scope from configuration to both the frontend build and the API `AzureAd` settings
- [ ] 5.4 Confirm the Static Web App linked backend still resolves after redeployment
- [ ] 5.5 Verify simulation SSE end to end against a deployed Static Web App: confirm events arrive incrementally rather than all at once on completion, and that a long-running simulation is not cut off
- [ ] 5.6 Treat a failure here as possibly pre-existing rather than a regression, since Server-Sent Events have only ever been exercised through the Vite development proxy
- [ ] 5.7 If SSE does not survive the linked backend, evaluate pointing the frontend directly at the API with CORS, or replacing SSE with polling for the simulation console, and record the outcome

## 6. Extend to remaining environments

- [ ] 6.1 Run `-WhatIf` then deploy against `bat`
- [ ] 6.2 Run `-WhatIf` then deploy against `test`
- [ ] 6.3 Confirm `bat` and `test` Function Apps behave identically to their previous script-based deployments
- [ ] 6.4 Confirm the shared App Service Plan carries the dashboard API and all three environment Function Apps without contention that affects behaviour
- [ ] 6.5 Confirm `dev1` queue triggers resume normally after the plan migration and no message was left mid-processing

## 7. Retire superseded scripts and container artifacts

- [ ] 7.1 Confirm no environment is currently served by the Container Apps deployment
- [x] 7.2 Capture the reverse-proxy streaming requirements from `nginx.conf.template` into the deployment documentation before deleting it: unbuffered forwarding, no caching, and a read timeout long enough for a full simulation run
- [x] 7.3 Delete `AzureDeploy.AppService.UpdateAzure copy.ps1`
- [x] 7.4 Delete `AzureDeploy.AppService.API.config.ps1`
- [x] 7.5 Delete `AzureDeploy.API.ps1`, `AzureDeploy.Frontend.ps1`, `AzureDeploy.AppService.UpdateAzure.ps1`, and `AzureDeploy.Function.EIM.Customer.dev1.ps1` once their behaviour is reproduced
- [ ] 7.5a Delete `AzureDeploy.AppService.ps1` — gitignored, so deletion is irreversible
- [x] 7.6 Delete the Container Apps script `AzureDeploy.Update.ps1`
- [ ] 7.6a Delete the Container Apps script `AzureDeploy` — gitignored, so deletion is irreversible
- [x] 7.7 Delete `docker-compose.yml`, the three Dockerfiles, and `nginx.conf.template`
- [x] 7.8 Remove container-related entries from the `ARM.Integration.Wilson.slnx` Solution Items folder, and any `.env` keys used only by compose
- [x] 7.9 Confirm `run.bat` and `run-dashboard.bat` still work and local development is unaffected
- [x] 7.10 Confirm the `access_as_user` scope variant is gone once the Container Apps scripts are deleted, leaving `user_impersonation` everywhere

> 7.9: both scripts only invoke `dotnet run` and `npm run dev`. Neither referenced anything that was
> deleted, the solution builds clean without the Dockerfiles, and the Functions host started from
> `run.bat` is running now.
>
> 7.10: no configured `access_as_user` remains. The one live occurrence was a placeholder in a doc
> comment in `msal.ts`, corrected to `user_impersonation`. The remaining matches are in this
> change's own proposal and design, which describe the divergence as history and should stay.

## 8. Document configuration decisions

- [x] 8.1 Document that Entra `5e8a5341` / `317063cf` in `ARM.Integration.Dashboard.API/appsettings.json` is the local-development registration and is intentionally different from the deployed `446b6761` / `5d1e26b3`
- [x] 8.2 Record that the shared dashboard resources are suffixed `-dev` but serve all non-production environments, and that renaming to `-nonprod` is deferred
- [x] 8.3 Record that production is out of scope, shares nothing, and will be a separate stamp with its own resource group, Service Bus namespace, App Service Plan, dashboard, and database
- [x] 8.4 Record that the App Service Plan is shared across non-production environments as a deliberate cost decision, and that environments therefore share compute
- [x] 8.5 Record that containers are not used, that local development is `run.bat` plus the Vite dev proxy, and that reintroducing containers would mean rebuilding the artifacts deleted here

## 9. Documentation

- [x] 9.1 Document how to deploy a component and a full environment
- [x] 9.2 Document the configuration file format and every required key
- [x] 9.3 Document the procedure for onboarding a new environment
- [x] 9.4 Note that the Container Apps path was removed and why, so it is not reintroduced

> `Script/Docs/deployment.md` covers sections 8 and 9 together, because the decisions and the
> instructions are only useful next to each other.
>
> **7.2 was done before anything was deleted**, which was the point of ordering it that way. The
> three things `nginx.conf.template` got right for Server-Sent Events - `proxy_buffering off`,
> `proxy_cache off`, and a 3600 second read timeout - are recorded in the deployment document. The
> same requirement applies to the Static Web App linked backend, which has never been exercised with
> SSE at all. If the simulation console arrives all at once or is cut off, that is the cause, and it
> is pre-existing rather than a regression.
>
> **Deletion is split by recoverability.** Everything tracked by git is gone and can be restored
> from history. `AzureDeploy` and `AzureDeploy.AppService.ps1` are gitignored, so deleting them is
> irreversible - they are the only copy. Their behaviour is reproduced in `Deploy-Eim.ps1` and their
> values are in `Script/config/deploy.*.json`, but that is my assessment, not a verified one, and it
> was never checked against live Azure. Left for the owner to delete. Tracked as 7.5a and 7.6a.
>
> `.env.example` was compose-only by its own first line and nothing else read it, so it went with
> the compose files. The root `.env` is untracked and was left alone.
>
> The solution file's Solution Items folder now carries the two documents instead of the container
> artifacts.
