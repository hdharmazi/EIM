## ADDED Requirements

### Requirement: The SQL server and database are created with an Entra administrator
The `sql` component SHALL run only for a configuration without `uses`.

When the configured SQL server does not exist, `sql` SHALL create it with:
- Entra-only authentication
- the Entra administrator named by `sql.entraAdmin`, `sql.entraAdminObjectId` and `sql.entraAdminType`
- minimum TLS 1.2
- the `eim-*` tags

No SQL administrator login or password SHALL be created. `sql` SHALL fail before any change, naming the keys, when `sql.entraAdmin` or `sql.entraAdminObjectId` is blank or a placeholder and the server does not exist.

When the configured database does not exist, `sql` SHALL create it with `sql.sku` and `sql.backupRedundancy` and the `eim-*` tags.

On a server that carries the `eim-environment` tag, `sql` SHALL keep the firewall as `sql.firewall` defines:
- `appOutboundIps`: one rule named `app-outbound-<address with dashes>` for every possible outbound address of the configuration's apps. Stale `app-outbound-*` rules are deleted.
- `azureServices`: the rule `AllowAllWindowsAzureIps`.

`deploy.base.json` SHALL set `sql.sku` `S0`, `sql.backupRedundancy` `Local`, `sql.firewall` `appOutboundIps` and `sql.entraAdminType` `Group`.

#### Scenario: Production server created
- **WHEN** `sql` runs for `prd` with filled `sql.entraAdmin` and `sql.entraAdminObjectId`, and `sql-eim-wtc-prd` does not exist
- **THEN** `sql-eim-wtc-prd` is created with Entra-only authentication, that administrator and minimum TLS 1.2, and `sqldb-eim-wtc-prd` is created with SKU `S0` and `Local` backup redundancy

#### Scenario: Administrator not configured
- **WHEN** `sql` runs for `prdmck`, `sql-eim-wtc-prdmck` does not exist and `sql.entraAdminObjectId` is blank
- **THEN** `sql` fails naming `sql.entraAdmin` and `sql.entraAdminObjectId`, and no server is created

#### Scenario: Firewall follows the app addresses
- **WHEN** `sql` runs for `prd`, the server carries `eim-environment=prd`, and an `app-outbound-*` rule exists for an address no `prd` app has any more
- **THEN** a rule exists for every current outbound address of the `prd` apps, and the stale rule is deleted

#### Scenario: Refused for a using configuration
- **WHEN** `sql` is requested for `test`
- **THEN** the script refuses before any Azure call, naming `bat`

### Requirement: An existing SQL server is used as found
When the configured SQL server exists and does not carry the `eim-environment` tag, `sql` SHALL change nothing on it. It SHALL report:
- the Entra administrator, or its absence
- whether Entra-only authentication is enabled
- for each possible outbound address of the configuration's apps, whether a firewall rule or `AllowAllWindowsAzureIps` covers it

A missing Entra administrator, an uncovered address, or a configured `sql.entraAdminObjectId` that differs from the server's administrator SHALL be warnings that name the server and the address or key. They SHALL NOT be changes.

Preflight SHALL fail, naming the server and the manual step, when `schema` or `sqlusers` is requested and the server has no Entra administrator.

On `azql-wtc-sandbox` the script SHALL NOT enable Entra-only authentication, so SQL logins used by existing tools keep working.

#### Scenario: Non-production server reported only
- **WHEN** `sql` runs for `bat` and `azql-wtc-sandbox` has no `eim-environment` tag
- **THEN** no command changes the server, its firewall, its administrator or its authentication mode, and the report lists the administrator and the firewall coverage of the outbound addresses of `asp-eim-wtc-bat`

#### Scenario: Uncovered outbound address
- **WHEN** `sql` runs for `bat` and an outbound address of `azf-eim-gateway-wtc-bat` is covered by no rule and `AllowAllWindowsAzureIps` is absent
- **THEN** `sql` warns naming `azql-wtc-sandbox` and the address, and succeeds

#### Scenario: Migrations before the administrator is set
- **WHEN** `schema` runs for `bat` and `azql-wtc-sandbox` has no Entra administrator
- **THEN** preflight fails naming `azql-wtc-sandbox` and stating that its Entra administrator must be set by hand first, and no migration is applied

### Requirement: Applications connect to the database with their managed identity
`settings` SHALL write the database connection of every app as a connection string with no credential:

```
Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;
```

The settings that hold it are:
- `EIMDB` on the Automation app and the gateway
- `ConnectionStrings__EIMDB` on the dashboard API

No application setting SHALL hold a SQL password.

`ARM.Integration.Core` SHALL reference `Microsoft.Data.SqlClient.Extensions.Azure`. Through it, every host that opens EIMDB connections SHALL resolve the `ActiveDirectoryManagedIdentity` and `ActiveDirectoryDefault` authentication providers. That covers the Automation host, the gateway and the dashboard API. Local development MAY keep SQL or Windows authentication against a local database.

#### Scenario: Automation connection string
- **WHEN** `settings` runs for `test`
- **THEN** `EIMDB` on `azf-eim-automation-wtc-test` names `azql-wtc-sandbox.database.windows.net` and `eimdb` with `Authentication=Active Directory Managed Identity`, and has no `Password` or `User ID`

#### Scenario: Provider available in every host
- **WHEN** `Wilson.Integration.Tests` asks `SqlAuthenticationProvider.GetProvider` for `ActiveDirectoryManagedIdentity` and `ActiveDirectoryDefault`
- **THEN** both return a provider

#### Scenario: Missing user surfaces as a connection failure
- **WHEN** the Automation app opens EIMDB and its identity has no database user
- **THEN** the open fails with a `SqlException`, which is classified exactly as a SQL login failure is classified today

### Requirement: Each deployed app identity has a contained database user
The `sqlusers` component SHALL create a contained database user, named after the app, for the system-assigned identity of every app the configuration deploys:
- owner configurations: the gateway, the dashboard API and their own Automation app
- configurations with `uses`: their own Automation app

Each user SHALL be created in the configured database, with `CREATE USER [<app>] WITH SID = <identity client id as binary(16)>, TYPE = E`. Names and identifiers SHALL be passed as parameters and quoted with `QUOTENAME`.

Each user SHALL be a member of `db_datareader` and `db_datawriter` and SHALL be granted `EXECUTE`.

`sqlusers` SHALL:
- connect with an access token for `https://database.windows.net/` from the signed-in Azure CLI identity
- read each identity's client id from the app's managed identity resource, falling back to the service principal's application id
- skip creating a user that exists with the expected SID, and still ensure its roles
- fail, naming the user, when a user with the app's name exists with a different SID; it SHALL NOT drop it
- never drop a user, revoke a role or change a database row
- after the changes, list each user with its roles

With `-WhatIf`, `sqlusers` SHALL list the users and roles it would ensure and SHALL NOT open a SQL connection.

#### Scenario: Users for bat
- **WHEN** `sqlusers` runs for `bat`
- **THEN** `eimdb` has the users `azf-eim-gateway-wtc-bat`, `wtc-dashboard-api-dev` and `azf-eim-automation-wtc-bat`, each of type `E`, each a member of `db_datareader` and `db_datawriter` with `EXECUTE`, and the output lists them with their roles

#### Scenario: Using configuration grants only its own app
- **WHEN** `sqlusers` runs for `test`
- **THEN** only the user `azf-eim-automation-wtc-test` is created or checked in `eimdb`

#### Scenario: Second run changes nothing
- **WHEN** `sqlusers` runs twice for `bat`
- **THEN** the second run creates no user and reports the same users and roles

#### Scenario: Recreated app
- **WHEN** `azf-eim-automation-wtc-bat` was recreated with a new identity and `sqlusers` runs for `bat`
- **THEN** it fails naming the user `azf-eim-automation-wtc-bat` and stating that the old user must be dropped by hand, and no user is dropped

#### Scenario: App without an identity
- **WHEN** `sqlusers` runs for `test` and `azf-eim-automation-wtc-test` has no system-assigned identity
- **THEN** it fails naming the app and stating that `infra` must run first

#### Scenario: Preview opens no connection
- **WHEN** `sqlusers` runs for `bat` with `-WhatIf`
- **THEN** it lists the three users and their roles, and no SQL connection is opened

#### Scenario: Deployer not an administrator
- **WHEN** the signed-in identity is not the server's Entra administrator or a member of its group
- **THEN** `sqlusers` fails naming the server and the identity and stating that the identity must be added to the administrator group, without an access token in the message

### Requirement: The migrator connects with the deployer's Entra identity
`schema` SHALL pass the migrator `EIMDB_MIGRATOR_CONNECTION` with `Authentication=Active Directory Default`, built from the resolved server and database, and `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` in the child process environment. Before the migrator starts, the database step SHALL obtain one access token for `https://database.windows.net/` through the Azure CLI, so the token is cached for the rest of the step.

`ARM.Integration.DbMigrator` SHALL accept such a connection string without a code change, and its usage text SHALL show an Entra example.

#### Scenario: Pipeline migration
- **WHEN** `schema` runs in `Deploy_bat`
- **THEN** the migrator signs in to `azql-wtc-sandbox`/`eimdb` as the `sc-eim-wtc-nonprod` principal, and no SQL login is used

#### Scenario: Operator migration
- **WHEN** an operator runs `Deploy-Wilson.ps1 -Environment bat -Component myip,schema,myipremove` after `az login`
- **THEN** the migrator signs in as the operator's Entra account

### Requirement: Database access is granted only on request
`sqlusers` writes to the database, including the shared `eimdb`. It SHALL run in a pipeline only when a manual run selects `grantDatabaseAccess`, after the stage approval, inside the database step between `myip` and `myipremove`.

The first `sqlusers` run against `eimdb` SHALL be performed by the user, previewed with `-WhatIf` first. The build agent SHALL NOT run it.

#### Scenario: Merge run grants nothing
- **WHEN** a run triggered by a merge to `main` deploys to `bat`
- **THEN** no database user is created or changed

#### Scenario: Requested and approved
- **WHEN** a manual `eim` run selects `grantDatabaseAccess` and `Deploy_bat` is approved
- **THEN** `sqlusers` runs for `bat` after `myip`, and `myipremove` runs even when `sqlusers` fails
