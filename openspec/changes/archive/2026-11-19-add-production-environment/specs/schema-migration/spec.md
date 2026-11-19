## ADDED Requirements

### Requirement: Migrations are safe on every stamp's database
The same versioned migrations SHALL be applied to the non-production `eimdb` (by `eim`, from `Deploy_shared`) and to the production database `sqldb-eim-wtc-prd` (by `eim-prd`, from `Deploy_prdshared`). The two databases SHALL share one migration history. A migration SHALL NOT branch on the database name, server or ENVIRONMENT values present. Every migration SHALL be safe and idempotent on both a database that has run every earlier migration for years and a database created empty for a new stamp. A parity check in a seed migration SHALL count only the rows that migration is responsible for, never the whole table. A seed that lists ENVIRONMENT values SHALL include `PRD` alongside `BAT`, `DEV`, `DEV2` and `TEST`.

#### Scenario: New production database
- **WHEN** the migrator runs against an empty `sqldb-eim-wtc-prd`
- **THEN** migrations 001 to the latest apply in order without error, and the journal lists the same scripts as `eimdb`

#### Scenario: A new database migrates from 001 with no manual step
- **WHEN** the migrator runs against a database created empty, with no trigger, procedure or table created by hand
- **THEN** `002a_create_modifieddatetime_trigger.sql` creates the `MODIFIEDDATETIME` trigger on `MESSAGEQUEUETABLE` before `003_backfill_truncated_sourceentity.sql` disables and re-enables it, every versioned script and then every repeatable script applies without error, and a second run applies no versioned script

#### Scenario: The trigger bootstrap on a database that already has the trigger
- **WHEN** the migrator runs against `eimdb`, which has the `MODIFIEDDATETIME` trigger and has journaled `003`
- **THEN** `002a` is applied as the pending script, changes nothing but the journal, and the repeatable trigger script then re-applies its definition without error

#### Scenario: Production migration in its own stage
- **WHEN** an `eim-prd` run with `runMigrations` selected is approved at `eim-wtc-prdshared`
- **THEN** the migrator runs once, in `Deploy_prdshared`, against the database named in the `eim-wtc-prd` group, before any production code is deployed, and `Deploy_prd` does not run it

#### Scenario: Migrator pointed at the wrong stamp
- **WHEN** `schema` runs for `prdshared` and `EIM_DEPLOY_EIMDB` names `azql-wtc-sandbox`
- **THEN** preflight fails and no migration is applied
