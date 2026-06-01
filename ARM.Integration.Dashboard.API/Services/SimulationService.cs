using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.EIM;
using Azure.Messaging.ServiceBus;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ARM.Integration.Dashboard.API.Services;

public class SimulationService(IConfiguration config)
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _compact  = new() { WriteIndented = false };

    private string SbConn          => config["Simulation:ServiceBusConnectionString"] ?? "";
    private string D365Queue        => config["Simulation:D365Queue"]                  ?? "";
    private string CaptureQueue     => config["Simulation:CaptureToD365Queue"]         ?? "";
    private string CaptureRespQueue => config["Simulation:CaptureToD365ResponseQueue"] ?? "";
    private string CaptureOutQueue  => config["Simulation:D365ToCaptureQueue"]         ?? "";

    public async IAsyncEnumerable<SimulationEvent> RunAsync(
        SimulationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SbConn))
        {
            yield return Err("Simulation:ServiceBusConnectionString is not configured in appsettings.");
            yield return Done();
            yield break;
        }

        yield return Info($"Scenario: {request.Scenario}");

        // Capture start time so Listen can discard stale messages enqueued before this run
        var startedAt = DateTimeOffset.UtcNow;
        var f  = request.Fields;

        IAsyncEnumerable<SimulationEvent> steps = request.Scenario switch
        {
            "d365-customer-create"             => D365CustomerCreate(f, ct, startedAt),
            "d365-customer-update"             => D365CustomerUpdate(f, ct, startedAt),
            "d365-customer-delete"             => D365CustomerDelete(f, ct, startedAt),
            "d365-contact-create"              => D365ContactCreate(f, ct, startedAt),
            "d365-contact-update"              => D365ContactUpdate(f, ct, startedAt),
            "d365-contact-delete"              => D365ContactDelete(f, ct, startedAt),
            "d365-contact-create-no-customer"  => D365ContactCreateNoCustomer(f, ct, startedAt),
            "d365-address-create"              => D365AddressCreate(f, ct, startedAt),
            "d365-address-update"              => D365AddressUpdate(f, ct, startedAt),
            "d365-address-delete"              => D365AddressDelete(f, ct, startedAt),
            "capture-customer-create"          => CaptureCustomerCreate(f, ct, startedAt),
            "capture-customer-update"          => CaptureCustomerUpdate(f, ct, startedAt),
            "capture-customer-delete"          => CaptureCustomerDelete(f, ct, startedAt),
            "capture-contact-create"           => CaptureContactCreate(f, ct, startedAt),
            "capture-contact-update"           => CaptureContactUpdate(f, ct, startedAt),
            "capture-contact-delete"           => CaptureContactDelete(f, ct, startedAt),
            "capture-partycontact-create"      => CapturePartyContactCreate(f, ct, startedAt),
            "capture-partycontact-update"      => CapturePartyContactUpdate(f, ct, startedAt),
            "capture-partycontact-delete"      => CapturePartyContactDelete(f, ct, startedAt),
            "capture-address-create"           => CaptureAddressCreate(f, ct, startedAt),
            "capture-address-update"           => CaptureAddressUpdate(f, ct, startedAt),
            "capture-address-delete"           => CaptureAddressDelete(f, ct, startedAt),
            _                                  => UnknownScenario(request.Scenario)
        };

        await foreach (var e in steps) yield return e;
        yield return Done();
    }

    // ── D365 → Capture ───────────────────────────────────────────────────

    private async IAsyncEnumerable<SimulationEvent> D365CustomerCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalCreated", "mserp_custcustomerv3entity",
            new()
            {
                ["mserp_partynumber"]               = Get(f, "mserp_partynumber",               "000001234"),
                ["mserp_dataareaid"]                = Get(f, "mserp_dataareaid",                "WTC"),
                ["mserp_customeraccount"]           = Get(f, "mserp_customeraccount",           "CUST-SIM-001"),
                ["mserp_organizationname"]          = Get(f, "mserp_organizationname",          "Simulation Test Corp"),
                ["mserp_namealias"]                 = Get(f, "mserp_namealias",                 "SimTest"),
                ["mserp_customergroupid"]           = Get(f, "mserp_customergroupid",           "RETAIL"),
                ["mserp_employeeresponsiblenumber"] = Get(f, "mserp_employeeresponsiblenumber", "ARM001"),
                ["mserp_blocked"]                   = Get(f, "mserp_blocked",                   "No"),
                ["mserp_depositrequired"]           = Get(f, "mserp_depositrequired",           "No"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 Customer Create Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365CustomerUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var changedFields = new List<string> { "mserp_organizationname", "mserp_namealias" };
        var payload = BuildD365Event("OnExternalUpdated", "mserp_custcustomerv3entity",
            new()
            {
                ["mserp_partynumber"]               = Get(f, "mserp_partynumber",               "000001234"),
                ["mserp_dataareaid"]                = Get(f, "mserp_dataareaid",                "WTC"),
                ["mserp_customeraccount"]           = Get(f, "mserp_customeraccount",           "CUST-SIM-001"),
                ["mserp_organizationname"]          = Get(f, "mserp_organizationname",          "Simulation Test Corp UPDATED"),
                ["mserp_namealias"]                 = Get(f, "mserp_namealias",                 "SimTestUpd"),
                ["mserp_customergroupid"]           = Get(f, "mserp_customergroupid",           "RETAIL"),
                ["mserp_employeeresponsiblenumber"] = Get(f, "mserp_employeeresponsiblenumber", "ARM001"),
            }, changedFields);

        await foreach (var e in Send(D365Queue, payload, "D365 Customer Update Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365CustomerDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalDeleted", "mserp_custcustomerv3entity",
            new()
            {
                ["mserp_partynumber"]     = Get(f, "mserp_partynumber",     "000001234"),
                ["mserp_dataareaid"]      = Get(f, "mserp_dataareaid",      "WTC"),
                ["mserp_customeraccount"] = Get(f, "mserp_customeraccount", "CUST-SIM-001"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 Customer Delete Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365ContactCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalCreated", "mserp_smmcontactpersonv2entity",
            new()
            {
                ["mserp_dataareaid"]                    = Get(f, "mserp_dataareaid",                    "WTC"),
                ["mserp_contactpersonid"]               = Get(f, "mserp_contactpersonid",               "CONT-SIM-001"),
                ["mserp_firstname"]                     = Get(f, "mserp_firstname",                     "John"),
                ["mserp_lastname"]                      = Get(f, "mserp_lastname",                      "Doe"),
                ["mserp_middlename"]                    = Get(f, "mserp_middlename",                    "M"),
                ["mserp_associatedcontactnumber"]       = Get(f, "mserp_associatedcontactnumber",       "AR-038824"),
                ["mserp_associatedcontacttype"]         = Get(f, "mserp_associatedcontacttype",         "Customer"),
                ["mserp_wtc_associatedcustomeraccount"] = Get(f, "mserp_wtc_associatedcustomeraccount", "CUST-SIM-001"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 Contact Create Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365ContactUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var changedFields = new List<string> { "mserp_firstname", "mserp_lastname" };
        var payload = BuildD365Event("OnExternalUpdated", "mserp_smmcontactpersonv2entity",
            new()
            {
                ["mserp_dataareaid"]                    = Get(f, "mserp_dataareaid",                    "WTC"),
                ["mserp_contactpersonid"]               = Get(f, "mserp_contactpersonid",               "CONT-SIM-001"),
                ["mserp_firstname"]                     = Get(f, "mserp_firstname",                     "Jane"),
                ["mserp_lastname"]                      = Get(f, "mserp_lastname",                      "Smith"),
                ["mserp_middlename"]                    = Get(f, "mserp_middlename",                    "M"),
                ["mserp_associatedcontactnumber"]       = Get(f, "mserp_associatedcontactnumber",       "AR-038824"),
                ["mserp_associatedcontacttype"]         = Get(f, "mserp_associatedcontacttype",         "Customer"),
                ["mserp_wtc_associatedcustomeraccount"] = Get(f, "mserp_wtc_associatedcustomeraccount", "CUST-SIM-001"),
            }, changedFields);

        await foreach (var e in Send(D365Queue, payload, "D365 Contact Update Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365ContactDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalDeleted", "mserp_smmcontactpersonv2entity",
            new()
            {
                ["mserp_dataareaid"]              = Get(f, "mserp_dataareaid",              "WTC"),
                ["mserp_contactpersonid"]         = Get(f, "mserp_contactpersonid",         "CONT-SIM-001"),
                ["mserp_associatedcontactnumber"] = Get(f, "mserp_associatedcontactnumber", "AR-038824"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 Contact Delete Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365ContactCreateNoCustomer(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalCreated", "mserp_smmcontactpersonv2entity",
            new()
            {
                ["mserp_dataareaid"]      = Get(f, "mserp_dataareaid",      "WTC"),
                ["mserp_contactpersonid"] = Get(f, "mserp_contactpersonid", "CONT-SIM-SKIP"),
                ["mserp_firstname"]       = Get(f, "mserp_firstname",       "Skip"),
                ["mserp_lastname"]        = Get(f, "mserp_lastname",        "Me"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 Contact Create (no customer)", ct)) yield return e;
        yield return Info("→ Listening on Capture output queue (should NOT receive anything within 15s)...");
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(15), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365AddressCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalCreated", "mserp_wtc_custpostaladdressv2entity",
            new()
            {
                ["mserp_dataareaid"]             = Get(f, "mserp_dataareaid",             "wtc"),
                ["mserp_customeraccount"]        = Get(f, "mserp_customeraccount",        "AR-019739"),
                ["mserp_isprimary"]              = Get(f, "mserp_isprimary",              "{\"Value\":200000001}"),
                ["mserp_addresslocationroles"]   = Get(f, "mserp_addresslocationroles",   "Business"),
                ["mserp_addressdescription"]     = Get(f, "mserp_addressdescription",     "Main Office"),
                ["mserp_addresscity"]            = Get(f, "mserp_addresscity",            "DALLAS"),
                ["mserp_addresszipcode"]         = Get(f, "mserp_addresszipcode",         "75201"),
                ["mserp_addressstreet"]          = Get(f, "mserp_addressstreet",          "123 Main St"),
                ["mserp_addresscountryregionid"] = Get(f, "mserp_addresscountryregionid", "USA"),
                ["mserp_addressstate"]           = Get(f, "mserp_addressstate",           "TX"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 CustomerAddress Create Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365AddressUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var changedFields = new List<string>
        {
            "mserp_customeraccount", "mserp_addresscity", "mserp_addressstreet",
            "mserp_addressdescription", "mserp_addresslocationroles", "mserp_isprimary",
        };
        var payload = BuildD365Event("OnExternalUpdated", "mserp_wtc_custpostaladdressv2entity",
            new()
            {
                ["mserp_dataareaid"]             = Get(f, "mserp_dataareaid",             "wtc"),
                ["mserp_customeraccount"]        = Get(f, "mserp_customeraccount",        "AR-019739"),
                ["mserp_addresslocationid"]      = Get(f, "mserp_addresslocationid",      "000005203"),
                ["mserp_isprimary"]              = Get(f, "mserp_isprimary",              "{\"Value\":200000001}"),
                ["mserp_addresslocationroles"]   = Get(f, "mserp_addresslocationroles",   "Business;Delivery"),
                ["mserp_addressdescription"]     = Get(f, "mserp_addressdescription",     "Main Office"),
                ["mserp_addresscity"]            = Get(f, "mserp_addresscity",            "DALLAS"),
                ["mserp_addresszipcode"]         = Get(f, "mserp_addresszipcode",         "75201"),
                ["mserp_addressstreet"]          = Get(f, "mserp_addressstreet",          "456 Updated Ave"),
                ["mserp_addresscountryregionid"] = Get(f, "mserp_addresscountryregionid", "USA"),
                ["mserp_addressstate"]           = Get(f, "mserp_addressstate",           "TX"),
            }, changedFields);

        await foreach (var e in Send(D365Queue, payload, "D365 CustomerAddress Update Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> D365AddressDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var payload = BuildD365Event("OnExternalDeleted", "mserp_wtc_custpostaladdressv2entity",
            new()
            {
                ["mserp_dataareaid"]        = Get(f, "mserp_dataareaid",        "wtc"),
                ["mserp_customeraccount"]   = Get(f, "mserp_customeraccount",   "AR-019739"),
                ["mserp_addresslocationid"] = Get(f, "mserp_addresslocationid", "000005203"),
            });

        await foreach (var e in Send(D365Queue, payload, "D365 CustomerAddress Delete Event", ct)) yield return e;
        await foreach (var e in Listen(CaptureOutQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    // ── Capture → D365

    private async IAsyncEnumerable<SimulationEvent> CaptureCustomerCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var envelope = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "New",
            Entity   = "Customer",
            Data     = new()
            {
                DataAreaId                = Get(f, "dataAreaId",                "WTC"),
                PartyType                 = "Organization",
                OrganizationName          = Get(f, "organizationname",          "Capture Simulation Customer"),
                NameAlias                 = Get(f, "namealias",                 "CapSimCust"),
                CustomerGroupId           = Get(f, "customergroupid",           "RETAIL"),
                EmployeeResponsibleNumber = Get(f, "employeeresponsiblenumber", "ARM001"),
                Blocked                   = "No",
                DepositRequired           = "No",
            }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Customer Create", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureCustomerUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var account = Get(f, "customeraccount", "");
        if (string.IsNullOrEmpty(account)) { yield return Err("CustomerAccount is required."); yield break; }

        var data = new CaptureCustomerMessageContract
        {
            DataAreaId      = Get(f, "dataAreaId", "WTC"),
            CustomerAccount = account,
        };
        if (f.TryGetValue("organizationname",          out var v1) && !string.IsNullOrEmpty(v1)) data.OrganizationName          = v1;
        if (f.TryGetValue("namealias",                 out var v2) && !string.IsNullOrEmpty(v2)) data.NameAlias                 = v2;
        if (f.TryGetValue("customergroupid",           out var v3) && !string.IsNullOrEmpty(v3)) data.CustomerGroupId           = v3;
        if (f.TryGetValue("employeeresponsiblenumber", out var v4) && !string.IsNullOrEmpty(v4)) data.EmployeeResponsibleNumber = v4;

        var envelope = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
        {
            SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Update",
            Entity   = "Customer",
            Data     = data
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Customer Update", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureCustomerDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var account = Get(f, "customeraccount", "");
        if (string.IsNullOrEmpty(account)) { yield return Err("CustomerAccount is required."); yield break; }

        var envelope = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
        {
            SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Delete",
            Entity   = "Customer",
            Data     = new() { DataAreaId = Get(f, "dataAreaId", "WTC"), CustomerAccount = account }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Customer Delete", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureContactCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var data = new CaptureContactMessageContract
        {
            DataAreaId            = Get(f, "dataAreaId",            "wtc"),
            FirstName             = Get(f, "firstname",             "John"),
            LastName              = Get(f, "lastname",              "Doe"),
            AssociatedPartyNumber = Get(f, "associatedpartynumber", "000005502"),
        };
        var mn = Get(f, "middlename", "");
        if (!string.IsNullOrEmpty(mn)) data.MiddleName = mn;

        var envelope = new CaptureMessageEnvelope<CaptureContactMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "New",
            Entity   = "Contact",
            Data     = data
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Contact Create", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureContactUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var contactId = Get(f, "contactpersonid", "");
        if (string.IsNullOrEmpty(contactId)) { yield return Err("ContactPersonId is required."); yield break; }

        var data = new CaptureContactMessageContract
        {
            DataAreaId      = Get(f, "dataAreaId", "wtc"),
            ContactPersonId = contactId,
        };
        if (f.TryGetValue("firstname",             out var v1) && !string.IsNullOrEmpty(v1)) data.FirstName             = v1;
        if (f.TryGetValue("lastname",              out var v2) && !string.IsNullOrEmpty(v2)) data.LastName              = v2;
        if (f.TryGetValue("middlename",            out var v3) && !string.IsNullOrEmpty(v3)) data.MiddleName            = v3;
        if (f.TryGetValue("associatedpartynumber", out var v4) && !string.IsNullOrEmpty(v4)) data.AssociatedPartyNumber = v4;

        var envelope = new CaptureMessageEnvelope<CaptureContactMessageContract>
        {
            SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Update",
            Entity   = "Contact",
            Data     = data
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Contact Update", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureContactDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var contactId = Get(f, "contactpersonid", "");
        if (string.IsNullOrEmpty(contactId)) { yield return Err("ContactPersonId is required."); yield break; }

        var envelope = new CaptureMessageEnvelope<CaptureContactMessageContract>
        {
            SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Delete",
            Entity   = "Contact",
            Data     = new() { DataAreaId = Get(f, "dataAreaId", "WTC"), ContactPersonId = contactId }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture Contact Delete", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CapturePartyContactCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var data = new CapturePartyContactMessageContract
        {
            ContactPartyNumber = Get(f, "contactpartynumber", "000000706"),
            Locator     = Get(f, "locator",     "712-366-2241"),
            IsPrimary   = Get(f, "isprimary",   "Yes"),
            Type        = Get(f, "type",        "Phone"),
        };
        var ext             = Get(f, "locatorextension",    "");
        var desc            = Get(f, "description",         "");
        var custPartyNumber = Get(f, "customerpartynumber", "");
        if (!string.IsNullOrEmpty(ext))             data.LocatorExtension    = ext;
        if (!string.IsNullOrEmpty(desc))            data.Description         = desc;
        if (!string.IsNullOrEmpty(custPartyNumber)) data.CustomerPartyNumber = custPartyNumber;

        var envelope = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "New",
            Entity   = "PartyContact",
            Data     = data
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture PartyContact Create", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CapturePartyContactUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var contactPartyNumber  = Get(f, "contactpartynumber",  "");
        var customerPartyNumber = Get(f, "customerpartynumber", "");
        var electronicAddrId    = Get(f, "electronicaddressid", "");
        if (string.IsNullOrEmpty(contactPartyNumber) && string.IsNullOrEmpty(customerPartyNumber))
            { yield return Err("At least one of ContactPartyNumber or CustomerPartyNumber is required."); yield break; }
        if (string.IsNullOrEmpty(electronicAddrId)) { yield return Err("ElectronicAddressId is required."); yield break; }

        var envelope = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
        {
            SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Update",
            Entity   = "PartyContact",
            Data     = new()
            {
                ContactPartyNumber  = string.IsNullOrEmpty(contactPartyNumber)  ? null : contactPartyNumber,
                CustomerPartyNumber = string.IsNullOrEmpty(customerPartyNumber) ? null : customerPartyNumber,
                ElectronicAddressId = electronicAddrId,
                Locator             = Get(f, "locator",          "712-366-2241"),
                LocatorExtension    = Get(f, "locatorextension", ""),
                IsPrimary           = Get(f, "isprimary",        "Yes"),
                Description         = Get(f, "description",      ""),
            }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture PartyContact Update", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CapturePartyContactDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var contactPartyNumber  = Get(f, "contactpartynumber",  "");
        var customerPartyNumber = Get(f, "customerpartynumber", "");
        var electronicAddrId    = Get(f, "electronicaddressid", "");
        if (string.IsNullOrEmpty(contactPartyNumber) && string.IsNullOrEmpty(customerPartyNumber))
            { yield return Err("At least one of ContactPartyNumber or CustomerPartyNumber is required."); yield break; }
        if (string.IsNullOrEmpty(electronicAddrId)) { yield return Err("ElectronicAddressId is required."); yield break; }

        var envelope = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
        {
            SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Delete",
            Entity   = "PartyContact",
            Data     = new()
            {
                ContactPartyNumber  = string.IsNullOrEmpty(contactPartyNumber)  ? null : contactPartyNumber,
                CustomerPartyNumber = string.IsNullOrEmpty(customerPartyNumber) ? null : customerPartyNumber,
                ElectronicAddressId = electronicAddrId,
            }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture PartyContact Delete", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureAddressCreate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var envelope = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "New",
            Entity   = "CustomerAddress",
            Data     = new()
            {
                DataAreaId             = Get(f, "dataAreaId",             "wtc"),
                CustomerAccount        = Get(f, "customeraccount",        "AR-019739"),
                AddressDescription     = Get(f, "addressdescription",     "Main Office"),
                AddressStreet          = Get(f, "addressstreet",          "123 Main St"),
                AddressCity            = Get(f, "addresscity",            "DALLAS"),
                AddressState           = Get(f, "addressstate",           "TX"),
                AddressZipCode         = Get(f, "addresszipcode",         "75201"),
                AddressCountryRegionId = Get(f, "addresscountryregionid", "USA"),
                AddressLocationRoles   = Get(f, "addresslocationroles",   "Business"),
                IsPrimary              = Get(f, "isprimary",              "Yes"),
            }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture CustomerAddress Create", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureAddressUpdate(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var account    = Get(f, "customeraccount",   "");
        var locationId = Get(f, "addresslocationid", "");
        if (string.IsNullOrEmpty(account))    { yield return Err("CustomerAccount is required.");    yield break; }
        if (string.IsNullOrEmpty(locationId)) { yield return Err("AddressLocationId is required."); yield break; }

        var data = new CaptureCustomerAddressMessageContract
        {
            DataAreaId        = Get(f, "dataAreaId", "wtc"),
            CustomerAccount   = account,
            AddressLocationId = locationId,
        };
        if (f.TryGetValue("addressstreet",        out var v1) && !string.IsNullOrEmpty(v1)) data.AddressStreet          = v1;
        if (f.TryGetValue("addresscity",          out var v2) && !string.IsNullOrEmpty(v2)) data.AddressCity            = v2;
        if (f.TryGetValue("addressstate",         out var v3) && !string.IsNullOrEmpty(v3)) data.AddressState           = v3;
        if (f.TryGetValue("addresszipcode",       out var v4) && !string.IsNullOrEmpty(v4)) data.AddressZipCode         = v4;
        if (f.TryGetValue("addresslocationroles", out var v5) && !string.IsNullOrEmpty(v5)) data.AddressLocationRoles   = v5;
        if (f.TryGetValue("isprimary",            out var v6) && !string.IsNullOrEmpty(v6)) data.IsPrimary              = v6;
        if (f.TryGetValue("addressdescription",   out var v7) && !string.IsNullOrEmpty(v7)) data.AddressDescription     = v7;

        var envelope = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Update",
            Entity   = "CustomerAddress",
            Data     = data
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture CustomerAddress Update", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    private async IAsyncEnumerable<SimulationEvent> CaptureAddressDelete(
        Dictionary<string, string> f, [EnumeratorCancellation] CancellationToken ct, DateTimeOffset startedAt = default)
    {
        var account    = Get(f, "customeraccount",   "");
        var locationId = Get(f, "addresslocationid", "");
        if (string.IsNullOrEmpty(account))    { yield return Err("CustomerAccount is required.");    yield break; }
        if (string.IsNullOrEmpty(locationId)) { yield return Err("AddressLocationId is required."); yield break; }

        var envelope = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
        {
            SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Action   = "Delete",
            Entity   = "CustomerAddress",
            Data     = new()
            {
                DataAreaId        = Get(f, "dataAreaId", "wtc"),
                CustomerAccount   = account,
                AddressLocationId = locationId,
            }
        };

        await foreach (var e in Send(CaptureQueue, envelope, "Capture CustomerAddress Delete", ct)) yield return e;
        await foreach (var e in Listen(CaptureRespQueue, TimeSpan.FromSeconds(60), ct, startedAt)) yield return e;
    }

    // ── Infrastructure helpers

    private async IAsyncEnumerable<SimulationEvent> Send(
        string queueName, object message, string description,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return Info($"Building payload for [{description}]...");

        var payload = JsonSerializer.Serialize(message, _compact);
        yield return new SimulationEvent("payload", payload);

        await using var client = new ServiceBusClient(SbConn);
        await using var sender = client.CreateSender(queueName);

        var sbMsg = new ServiceBusMessage(payload)
        {
            ContentType = "application/json",
            MessageId   = $"SIM-{Guid.NewGuid():N}"
        };

        await sender.SendMessageAsync(sbMsg, ct);
        yield return new SimulationEvent("success", $"✓ Sent [{description}] to '{queueName}'");
        yield return Info($"  MessageId: {sbMsg.MessageId}");
    }

    private async IAsyncEnumerable<SimulationEvent> Listen(
        string queueName, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct,
        DateTimeOffset? ignoreMessagesBefore = null)
    {
        yield return Info($"→ Listening on '{queueName}' (timeout: {timeout.TotalSeconds}s)...");

        await using var client   = new ServiceBusClient(SbConn);
        await using var receiver = client.CreateReceiver(queueName);

        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var msg = await receiver.ReceiveMessageAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1), ct);

            if (msg is null)
            {
                yield return new SimulationEvent("warning", $"⏱ No response received within {timeout.TotalSeconds}s on '{queueName}'.");
                yield break;
            }

            // Dead-letter stale messages so they don't loop back into the queue
            if (ignoreMessagesBefore.HasValue && msg.EnqueuedTime < ignoreMessagesBefore.Value)
            {
                yield return Info($"  ⏭ Skipping stale message (EnqueuedAt: {msg.EnqueuedTime:yyyy-MM-dd HH:mm:ss} UTC) — dead-lettering it.");
                await receiver.DeadLetterMessageAsync(msg, "StaleSimulationMessage", "Message was enqueued before this simulation run started.", ct);
                continue;
            }

            await receiver.CompleteMessageAsync(msg, ct);
            yield return new SimulationEvent("success", $"✓ Received message from '{queueName}'");
            yield return Info($"  MessageId: {msg.MessageId}");
            yield return Info($"  EnqueuedAt: {msg.EnqueuedTime:yyyy-MM-dd HH:mm:ss} UTC");

            var body = msg.Body.ToString();
            string receivedText;
            try
            {
                using var doc = JsonDocument.Parse(body);
                receivedText  = JsonSerializer.Serialize(doc.RootElement, _indented);
            }
            catch
            {
                receivedText = body;
            }
            yield return new SimulationEvent("received", receivedText);
            yield break;
        }
    }

    private static object BuildD365Event(
        string messageName,
        string entityName,
        Dictionary<string, string> attributes,
        List<string>? changedFields = null)
    {
        var inputParams = new List<object>
        {
            new
            {
                key   = "Target",
                value = new
                {
                    Attributes  = attributes.Select(kvp => new { key = kvp.Key, value = kvp.Value }).ToList(),
                    Id          = Guid.NewGuid().ToString(),
                    LogicalName = entityName
                }
            }
        };

        if (changedFields is not null)
            inputParams.Add(new { key = "ChangedFields", value = changedFields });

        return new
        {
            MessageName                      = messageName,
            PrimaryEntityName                = entityName,
            UserAzureActiveDirectoryObjectId = "00000000-0000-0000-0000-000000000000",
            UserId                           = "SimulationUser",
            InputParameters                  = inputParams,
            OperationCreatedOn               = DateTime.UtcNow.ToString("o"),
            OperationId                      = Guid.NewGuid().ToString()
        };
    }

    private static async IAsyncEnumerable<SimulationEvent> UnknownScenario(string scenario)
    {
        yield return Err($"Unknown scenario: '{scenario}'");
        await Task.CompletedTask;
    }

    private static string Get(Dictionary<string, string> f, string key, string def)
        => f.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;

    private static SimulationEvent Info(string text)  => new("info",    text);
    private static SimulationEvent Err(string text)   => new("error",   text);
    private static SimulationEvent Done()              => new("done",    "Simulation complete.");
}
