## 1. Deployment script

- [x] 1.1 Derive the gateway app and storage names without an environment suffix, defaulting to `azf-d365fsc-gateway-wtc-nonprod` and a matching storage account
- [x] 1.2 Add `gateway` to the shared-component set so it is refused against an environment that does not host it
- [x] 1.3 Gate on `gateway.hostsGateway`, reported with the same wording as `dashboard.hostsDashboard`
- [x] 1.4 Skip rather than fail the gateway when `all` is run against a non-hosting environment, so a full deployment of a normal environment still succeeds
- [x] 1.5 Name the App Insights resource for the shared gateway without an environment suffix
- [x] 1.6 Run `-WhatIf` for a hosting and a non-hosting environment and confirm the derived names and the skip behaviour

> 1.2 turned into more than intended. `all` already threw on `api`/`frontend` for any environment that
> did not host the dashboard, so `all` only ever worked against `bat` despite the documentation saying
> it runs every component. Adding gateway skipping alone would have left two shared components
> behaving differently in the same situation, so the rule is now uniform: naming a shared component
> explicitly is refused, asking for `all` skips it and says which.

## 2. Configuration

- [x] 2.1 Add `gateway.hostsGateway` to `deploy.example.json` with a note that exactly one environment sets it
- [x] 2.2 Decide which environment hosts the shared gateway and set the flag there, resolving the open question in `design.md` — `bat`, which already hosts the dashboard
- [x] 2.3 Set `gateway.baseUrl` on the hosting environment to the shared app's host name
- [x] 2.4 Set `hostsGateway` false on every other environment, leaving their existing gateway blocks in place while the old apps still serve consumers
- [x] 2.5 Confirm a missing `hostsGateway` fails with the key named rather than defaulting to false

## 3. Bicep and ad-hoc script consolidation

- [x] 3.1 Move `main.bicep`, `main-addqueue.bicep` and `main-addqueueTest.bicep` to `Script/Superseded/`
- [x] 3.2 Add a note in that folder recording that queue provisioning is now `Deploy-Eim.ps1 -Component queues`, and that these files hard-code environment prefixes
- [x] 3.3 SUPERSEDED by 3.10: `main-keyvault.bicep` was to be left in place as deferred work, but the decision to remove Bicep entirely means it is converted rather than kept
- [x] 3.4 Record that `main.bicep` declares a `queueNames` array it never uses, so nobody reading it later assumes it provisioned those queues
- [x] 3.5 Confirm nothing references the moved files
- [x] 3.6 Replace `Script/Bicep/Firewall` with a `firewall` component that reads the server from the connection string and names the rule per developer
- [x] 3.7 Keep `firewall` out of `all` — a deployment that quietly widens network access is not something to run by default
- [x] 3.8 Retire `delete-queue.ps1` without replacing it; queue deletion is deliberately not a component
- [x] 3.9 Strip the live Service Bus root key from `Script/Bicep/script.txt` and point it at the deployment script
- [x] 3.10 Replace `main-keyvault.bicep` with a `keyvault` component using `az keyvault`, keeping it out of `all`
- [x] 3.11 Remove the `Script/Bicep` folder entirely; no Bicep or ARM template remains in the repository
- [x] 3.12 Note that the vault's RBAC mode means creating it does not grant rights to write secrets, unlike the template's ARM parameter, and surface the required role on failure
- [x] 3.13 Write `Script/Docs/deploy-from-scratch.md` for standing the system up in an empty subscription

## 3a. Committed secrets

- [x] 3a.1 Add `ARM.Integration.D365FSC.Gateway/.gitignore` for `local.settings.json`, mirroring `ARM.Integration.EIM.Customer`
- [x] 3a.2 `git rm --cached` the gateway's `local.settings.json` so the SQL password stops being tracked
- [x] 3a.3 ACCEPTED RISK: the `sadmin` SQL password stays as-is during implementation. It remains readable in git history.
- [x] 3a.4 ACCEPTED RISK: the Service Bus `RootManageSharedAccessKey` stays as-is. It remains readable in git history.
- [x] 3a.5 Remove the key from `Script/SQL/_scratch/Working query.sql` — file deleted
- [x] 3a.6 Decided: rotation deferred, acceptable while this is pre-production

> Both credentials are still recoverable from git history by anyone with repo access, and untracking
> the files did not change that. The risk is accepted for now on the basis that this is not yet
> serving production traffic — which means rotation has to happen before it does, not merely when
> somebody remembers.

## 4. Documentation

- [x] 4.1 Add `gateway` to the component table in `Script/Docs/deployment.md`, which omits it today while claiming `all` runs every component
- [x] 4.2 Add the `gateway.*` keys to the configuration table, including `hostsGateway`
- [x] 4.3 State in Decisions that the gateway is shared, why it can be, and why `EIM.Customer` cannot
- [x] 4.4 Record the `-nonprod` naming decision alongside the existing `-dev` dashboard note, so the inconsistency is explained rather than discovered
- [x] 4.5 Update the architecture paragraph in `docs/azure-deployment-pipelines-setup.md` to cover the gateway
- [x] 4.6 Document that `eimdb_dev` is for local development only and shares a server with the live `eimdb`
- [x] 4.7 Add a section on adding an environment: queues, storage and Functions host are per environment; the gateway and dashboard are not

## 5. Consumer documentation

- [x] 5.1 Record in `Script/Docs/d365fsc-gateway.md` that one gateway serves every non-production environment, and that the D365 environment comes from the request path
- [x] 5.2 Explain that `eng-apps` and `eng-apps-dev` exist because keys do not cross applications, and that the reason disappears once consumers move to the shared app
- [x] 5.3 Record the cutover procedure: issue a key on the shared app, re-register it, change the consumer's base URL in the same step

## 6. Follow-on

- [x] 6.1 Reduce `add-gateway-key-verification` to a single gateway: the probe no longer needs to iterate, and the column recording which application accepted the key becomes unnecessary
- [x] 6.2 Keep the parts of that change that remain valuable — probing at all, rejected versus unreachable, the verification date, and loopback handling

## 7. Verification

- [x] 7.1 Deploy the shared gateway and confirm it answers on the `-nonprod` host name
- [x] 7.2 Confirm a request routed to a D365 environment returns the same result as the corresponding per-environment gateway did
- [x] 7.3 Confirm `-Component gateway` against a non-hosting environment is refused
- [x] 7.4 Confirm `-Component all` against a non-hosting environment deploys everything else and skips the gateway
- [x] 7.5 Leave the per-environment gateway apps running and confirm existing consumers are unaffected — `eng-apps` and `eng-apps-dev` hashes untouched
- [x] 7.6 Fix `deploy.dev.json`, which pointed at deleted `dev1` resources

> 7.6 was worse than a stale suffix. `azf-eim-customer-wtc-dev1`, `stwtceimcustomerdev1` and
> `asp-appservice-p0v3-linux-dev1` do not exist in Azure at all. Deriving the names instead would have
> been wrong too: the real storage account is `wtcd365fscintegrationdev`, which does not follow the
> pattern, so it stays an explicit override. Checked against the deployed resources rather than
> assumed.

> 7.2 used a temporary `gw-smoke` consumer with its own key and a single DEV GET grant, rather than
> repointing `eng-apps`. Repointing would have replaced a working consumer's key hash and broken it
> against the gateway it currently uses. The rows were deleted afterwards.

## 8. Cutover

- [x] 8.1 Issue a host key on the shared gateway for each real consumer
- [x] 8.2 Re-register each key and change the consumer's base address in the same step
- [x] 8.3 Confirm no traffic remains on `azf-d365fsc-gateway-wtc-dev` and `-test`
- [x] 8.4 Delete the per-environment gateway apps and their storage accounts, as a separate decision once 8.3 holds
- [x] 8.5 Remove the `eng-apps` key created on the shared gateway during verification if cutover does not proceed soon

> The cutover this section was written for no longer exists. `eng-apps` and `eng-apps-dev` have been
> deleted from `GATEWAYCONSUMER`; App Insights shows requests for `eng-apps` failing with
> `reason=unknown consumer`. `test-1` is the only consumer, and it was created directly against the
> shared gateway, so there was nothing to migrate.
>
> Verified 2026-09-16 after the operator acted: `azf-d365fsc-gateway-wtc-nonprod` is the only gateway
> app left, the shared gateway holds only `default` and `test-1`, and consumer `test-1` is bound to
> the `test-1` key rather than the shared `default` one.
>
> 8.3 was checked before deletion: one request each to `-dev` and `-test` in seven days, both from
> this session's diagnostics.
