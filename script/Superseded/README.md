# Superseded

These files provisioned or maintained Azure resources before the deployment script covered them. They are kept
because they record what was done historically, not because they should be run.

| File | Replaced by |
|---|---|
| `main.bicep`, `main-addqueue.bicep`, `main-addqueueTest.bicep` | `Deploy-Wilson.ps1 -Environment <env> -Component infra`, which creates the five queues per environment |
| `main-keyvault.bicep` | nothing; retired (see below) |
| `Firewall.ps1` | `Deploy-Wilson.ps1 -Environment nonprod -Component myip`, and `myipremove` to delete the rule |
| `delete-queue.ps1` | nothing, deliberately (see below) |

There is no Bicep in this repository any more. Everything on the Azure side is a component of
`script/deployment/Deploy-Wilson.ps1`, so there is one way to deploy and one place that reads a configuration.
See [docs/deployment/deployment.md](../../docs/deployment/deployment.md).

## Queue templates

- `main.bicep` and `main-addqueue.bicep` hard-code `bat-` and `test-` queue names. Adding an
  environment meant editing a template by hand, and forgetting one produced an environment whose
  messages dead-letter with `NoRoute`.
- `main.bicep` declares a `queueNames` array that nothing references, and creates a single queue from
  a separate `queueName` parameter. It does not provision the queues it appears to, so reading it to
  find out what exists gives the wrong answer.
- The queue name sets here disagree with each other and with the five queues the deployment script
  derives. `main.bicep` lists per-entity queues that the current design does not use.

## `Firewall.ps1`

Added the running machine's public IP to the sandbox SQL server. Replaced by the `myip` component, which differs
in three ways that matter:

- The server is read from the `EIM_DEPLOY_EIMDB` connection string and cross-checked against the configured SQL
  server, so a rule cannot be added to one server while deploying against another.
- It uses `az` like the rest of the script, rather than requiring the `Az` PowerShell module.
- The rule is named for the caller (`AllowDev-<username>`, or `eim-deploy-<build id>` in the pipeline) rather
  than `AllowMyIP`, so rules can be attributed and removed. A single shared name silently moves to whoever ran it
  last. `myipremove` deletes the rule, and the pipeline always runs it.

## `delete-queue.ps1`

Deleted a hard-coded list of per-entity `test-` queues that the current design does not use. It was a
one-off cleanup and has served its purpose.

It has deliberately **not** been turned into a deployment component. A queue holds undelivered
messages, and a deletion step sitting alongside provisioning steps is one mistyped environment away
from discarding live traffic. Deleting a queue should stay a considered act.

## `main-keyvault.bicep`

Provisioned a vault and stored the Service Bus connection string as a secure deployment parameter. Retired with
no replacement: **nothing in the system reads from the vault.** Connection strings come from `ENVIRONMENTTABLE`
and app settings, and the deployment script takes its two secrets from the environment. A vault populated by a
deployment would only be a second copy of each secret to rotate alongside the first. The existing vault,
`kv-d365fsc-wtc-nonprod`, is kept; Key Vault references are designed for and deferred.
