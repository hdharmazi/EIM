# Superseded

These files provisioned or maintained Azure resources before `script/deployment/Deploy-Eim.ps1` covered them.
They are kept because they record what was done historically, not because they should be run.

| File | Replaced by |
|---|---|
| `main.bicep`, `main-addqueue.bicep`, `main-addqueueTest.bicep` | `Deploy-Eim.ps1 -Component queues` |
| `main-keyvault.bicep` | `Deploy-Eim.ps1 -Component keyvault` |
| `Firewall.ps1` | `Deploy-Eim.ps1 -Component firewall` |
| `delete-queue.ps1` | nothing, deliberately — see below |

There is no Bicep in this repository any more. Everything Azure-side is a component of
`script/deployment/Deploy-Eim.ps1`, so there is one way to deploy and one place that reads an environment's
configuration.

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

Added the running machine's public IP to the sandbox SQL server. Replaced by
`Deploy-Eim.ps1 -Component firewall`, which differs in three ways that matter:

- The server is read from the environment's connection string rather than hard-coded, so the rule
  cannot be added to one server while deploying against another.
- It uses `az` like the rest of the script, rather than requiring the `Az` PowerShell module.
- The rule is named for the developer rather than `AllowMyIP`, so rules can be attributed and
  removed. A single shared name silently moves to whoever ran it last.

## `delete-queue.ps1`

Deleted a hard-coded list of per-entity `test-` queues that the current design does not use. It was a
one-off cleanup and has served its purpose.

It has deliberately **not** been turned into a deployment component. A queue holds undelivered
messages, and a deletion step sitting alongside provisioning steps is one mistyped environment away
from discarding live traffic. Deleting a queue should stay a considered act.

## `main-keyvault.bicep`

Provisioned a vault and stored the Service Bus connection string as a secure deployment parameter.
Replaced by `Deploy-Eim.ps1 -Component keyvault`.

One behavioural difference to be aware of. The template passed the secret as an ARM parameter, so
control-plane rights were enough to write it. The script uses `az keyvault secret set`, which is a
data-plane call — so creating the vault no longer implies being able to write secrets into it. The
component detects that failure and prints the role assignment needed.

**Still true either way: nothing in the system reads from the vault.** Connection strings remain in
`ENVIRONMENTTABLE` and app settings in plaintext. Provisioning the vault creates a second copy of
each secret that must be rotated alongside the first. The component exists so the capability is not
lost, not because the work is finished.
