**Ticket:** #5236 — *1197-Customer address update should not send new action to Capture message payload*

## Why

When a user edits an existing customer postal address in D365 F&SC, Capture receives the change as
`Action = "New"` instead of `"Update"`. BAT messages **78829** and **78832** show it: both are
`OnExternalCreated` data events for the same address location `000312031`, 47 seconds apart (the
create, then an edit of the street), and both were forwarded to Capture as `New`.

The cause is on the D365 side, not in the mapping. `LogisticsPostalAddress` is a date-effective
table and D365 edits an address by **creating a new version row** (`ValidTimeStateUpdate::CreateNewTimePeriod`,
see `WTC_CustPostalAddressV2Entity.initializeEntityDataSource`). The new row has a new `RecId` and a
new `ValidFrom`, so Dataverse sees a new entity record and raises `OnExternalCreated`; the
integration's base rule (`OnExternalCreated → New`, `D365EventForwardOrchestration.ParseAndMap`)
then faithfully reports a creation. Over the last 60 days the address entity produced 45
`OnExternalCreated` events against 32 `OnExternalUpdated` (the latter are in-place corrections:
roles and description only). Nothing in today's payload distinguishes a first version from a later
one: the two messages differ only in `mserp_addressvalidfrom` and the street text.

## What Changes

- **D365 F&SC (WTC model, repo `C:\Custom`)**: add a read-only computed `NoYes` field
  `WTC_HasPriorVersion` to `WTC_CustPostalAddressV2Entity`. It is `Yes` when another
  `LogisticsPostalAddress` row exists for the same `LogisticsLocation` with an earlier `ValidFrom`,
  i.e. this row supersedes a previous version of the same address. Same pattern as `WTC_PartyRole`
  in #4883.
- **Dataverse**: refresh the `mserp_wtc_custpostaladdressv2entity` virtual entity so
  `mserp_wtc_haspriorversion` appears in data event payloads.
- **Integration engine (`ARM.Integration.EIM`)**: give `D365EventForwardOrchestration` a
  context-aware action hook (`ResolveAction`) that runs after the message-name mapping and before
  `ForcedAction`. Default behaviour is unchanged.
- **Integration (`CustomerAddress_To_Capture`)**: when the derived action is `New` and
  `mserp_wtc_haspriorversion` resolves to `Yes`, forward `Action = "Update"` with the full address
  body. When the attribute is absent or not `Yes`, keep `New` (today's behaviour), so the
  integration can ship before or after the D365 package.
- Delete forwarding is untouched: `CustomerAddressDelete_To_Capture` still forces `Delete`.

## Capabilities

### New Capabilities
- `customer-address-update-action`: how the D365 → Capture customer address flow decides between
  `New` and `Update` for a data event, including the prior-version marker, its fallback, and the
  payload shape of a version-based update.

### Modified Capabilities
- `customer-address-delete-forwarding`: the scenario *"The New/Update route is unaffected"* states
  that the create/update route derives its action from the message name alone; it now also consults
  the prior-version marker.

## Impact

- D365 metadata (`C:\Custom`, branch to be created as `bug/5236/<slug>`):
  `Metadata/WTC/WTC/AxDataEntityView/WTC_CustPostalAddressV2Entity.xml` (new unmapped computed
  field + static computed-column method). No table or staging change. Requires WTC model build, DB
  sync, and a package deployment per environment.
- Dataverse: virtual entity metadata refresh for `mserp_wtc_custpostaladdressv2entity` per
  environment. Data event subscription unchanged.
- Integration code: `ARM.Integration.EIM/Orchestration/D365EventForwardOrchestration.cs`,
  `Wilson.Integration.Orchestrations/Orchestrations/CustomerAddress_To_Capture.cs`,
  tests in `Wilson.Integration.Tests`, one new fixture under `Wilson.Integration.Tests/TestData`.
- No change to routes, `OrchestrationRegistry`, SQL schema, Service Bus queues, the Capture message
  contract, the Dashboard, or failure classification (no new exception types).
- Ordering of deployments is free: integration first → behaviour identical to today until the D365
  field arrives; D365 first → the extra attribute is ignored until the integration ships.

## Non-goals

- Changing how D365 versions addresses (switching UI edits to `Correction` mode) or suppressing the
  `OnExternalUpdated` that expires the previous version (already dropped by the tracked-field filter).
- Backfilling or re-sending the historical `New` messages already delivered to Capture (78829/78832
  and the other repeat-create locations); see design open questions.
- Deciding `New` vs `Update` from EIM's own message history (`MESSAGEQUEUETABLE`), or always sending
  `Update`.
- Changing `Customer_To_Capture`, `Contact_To_Capture`, `PartyContact_To_Capture`, or the
  Capture → D365 direction.
- Simulation console scenarios for a version-based address update.
