## ADDED Requirements

### Requirement: Production routes are seeded
Migration `015_seed_prd_routes.sql` SHALL insert, only where absent, one `ROUTETABLE` row per route for ENVIRONMENT `PRD`:
- The nine routes seeded by 006 SHALL be `ENABLED = 1` with `ONPAUSE = 'Hold'`.
- The customer address delete route (`D365FSC`, `mserp_wtc_custpostaladdressdeletedv2entity`, `CustomerAddressDelete_To_Capture`) SHALL be `ENABLED = 0` with `ONPAUSE = 'Hold'`, so that production deletes are held rather than dead-lettered until the route is enabled.

The migration SHALL NOT change an existing row. It SHALL fail with a parity error unless:
- exactly ten `PRD` rows exist for these routes
- every orchestration type is a registered type
- every row still carrying `MODIFIEDBY = 'Migrations.015_seed_prd_routes.sql'` matches its seed (enabled state, `Hold`, orchestration type)

Rows an operator has since changed are not checked against the seed.

#### Scenario: Production routes resolve
- **WHEN** 015 has run and an Automation message arrives with ENVIRONMENT `prd` for `mserp_custcustomerv3entity`
- **THEN** the route resolves to `Customer_To_Capture` and the message is processed

#### Scenario: Production deletes held
- **WHEN** 015 has run and a `PRD` message arrives for `mserp_wtc_custpostaladdressdeletedv2entity`
- **THEN** the message is stored as `Held` with reason "route is paused", not dead-lettered as `NoRoute`

#### Scenario: Safe on the non-production database
- **WHEN** 015 runs against the non-production `eimdb`
- **THEN** ten `PRD` rows are added, and every `BAT`, `DEV`, `DEV2` and `TEST` row is unchanged

#### Scenario: Operator change preserved
- **WHEN** an operator has enabled the `PRD` delete route and 015 is run again against a restored journal
- **THEN** the route stays enabled
