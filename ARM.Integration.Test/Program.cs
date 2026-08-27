using Wilson.Integration.Orchestrations;
using ARM.Integration.Core.Helpers;
using ARM.Integration.EIM;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace ARM.Integration.Test
{
    /// <summary>
    /// Simulation testing program that sends messages to Service Bus queues and
    /// listens for responses, simulating the full EIM orchestration pipeline.
    /// 
    /// Flow 1 (D365 FSC → Capture):
    ///   Sends a D365 EventContext to the D365 queue → Azure Function picks it up →
    ///   runs Customer_To_Capture / Contact_To_Capture orchestration → forwards to Capture queue.
    ///
    /// Flow 2 (Capture → D365 FSC):
    ///   Sends a CaptureCustomerMessageContract to the Capture queue → Azure Function picks it up →
    ///   runs Customer_To_D365FSC orchestration → creates/updates in D365 → sends response.
    /// </summary>
    internal class Program
    {
        // ─── Configuration ────────────────────────────────────────────────
        // Values are loaded from local.settings.json at startup.
        private static string ServiceBusConnectionString = "";
        private static string D365Queue                  = "";
        private static string CaptureCustomerQueue       = "";
        private static string CaptureCustomerResponseQueue = "";
        private static string CaptureOutputQueue         = "";

        static async Task<int> Main(string[] args)
        {
            // ── Load local.settings.json ──────────────────────────────────
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("local.settings.json", optional: false, reloadOnChange: false)
                .Build();

            ServiceBusConnectionString    = config["ServiceBusConnectionString"]    ?? throw new InvalidOperationException("ServiceBusConnectionString not set in local.settings.json");
            D365Queue                     = config["D365Queue"]                     ?? throw new InvalidOperationException("D365Queue not set in local.settings.json");
            CaptureCustomerQueue          = config["CaptureToD365Queue"]            ?? throw new InvalidOperationException("CaptureToD365Queue not set in local.settings.json");
            CaptureCustomerResponseQueue  = config["CaptureToD365ResponseQueue"]    ?? throw new InvalidOperationException("CaptureToD365ResponseQueue not set in local.settings.json");
            CaptureOutputQueue            = config["D365ToCaptureQueue"]            ?? throw new InvalidOperationException("D365ToCaptureQueue not set in local.settings.json");

            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║   EIM Integration Simulation Test Program   ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");
            Console.WriteLine();

            while (true)
            {
                Console.WriteLine("Select a simulation to run:");
                Console.WriteLine("  1. D365 → Capture: New Customer (sends D365 event)");
                Console.WriteLine("  2. D365 → Capture: Update Customer (sends D365 event)");
                Console.WriteLine("  3. D365 → Capture: Delete Customer (sends D365 event)");
                Console.WriteLine("  4. D365 → Capture: New Contact (sends D365 event)");
                Console.WriteLine("  5. D365 → Capture: Update Contact (sends D365 event)");
                Console.WriteLine("  6. D365 → Capture: Delete Contact (sends D365 event)");
                Console.WriteLine("  7. Capture → D365: New Customer (sends Capture message)");
                Console.WriteLine("  8. Capture → D365: Update Customer (sends Capture message)");
                Console.WriteLine("  9. Capture → D365: Delete Customer (sends Capture message)");
                Console.WriteLine("  10. Capture → D365: New Contact (sends Capture message)");
                Console.WriteLine("  11. Capture → D365: Update Contact (sends Capture message)");
                Console.WriteLine("  12. Capture → D365: Delete Contact (sends Capture message)");
                Console.WriteLine("  14. Capture → D365: New PartyContact (sends Capture message)");
                Console.WriteLine("  15. Capture → D365: Update PartyContact (sends Capture message)");
                Console.WriteLine("  16. Capture → D365: Delete PartyContact (sends Capture message)");
                Console.WriteLine("  17. D365 → Capture: New Contact (no customer account - expect skip)");
                Console.WriteLine("  18. D365 → Capture: New CustomerAddress (sends D365 event)");
                Console.WriteLine("  19. D365 → Capture: Update CustomerAddress (sends D365 event)");
                Console.WriteLine("  20. D365 → Capture: Delete CustomerAddress (sends D365 event)");
                Console.WriteLine("  21. Capture → D365: New CustomerAddress (sends Capture message)");
                Console.WriteLine("  22. Capture → D365: Update CustomerAddress (sends Capture message)");
                Console.WriteLine("  23. Capture → D365: Delete CustomerAddress (sends Capture message)");
                Console.WriteLine("  99. Listen for messages on a queue");
                Console.WriteLine("  0. Exit");
                Console.Write("\nChoice: ");

                var choice = Console.ReadLine()?.Trim();

                try
                {
                    switch (choice)
                    {
                        case "1": await SimulateD365CustomerCreate(); break;
                        case "2": await SimulateD365CustomerUpdate(); break;
                        case "3": await SimulateD365CustomerDelete(); break;
                        case "4": await SimulateD365ContactCreate(); break;
                        case "5": await SimulateD365ContactUpdate(); break;
                        case "6": await SimulateD365ContactDelete(); break;
                        case "7": await SimulateCaptureCustomerCreate(); break;
                        case "8": await SimulateCaptureCustomerUpdate(); break;
                        case "9": await SimulateCaptureCustomerDelete(); break;
                        case "10": await SimulateCaptureContactCreate(); break;
                        case "11": await SimulateCaptureContactUpdate(); break;
                        case "12": await SimulateCaptureContactDelete(); break;
                        case "14": await SimulateCapturePartyContactCreate(); break;
                        case "15": await SimulateCapturePartyContactUpdate(); break;
                        case "16": await SimulateCapturePartyContactDelete(); break;
                        case "17": await SimulateD365ContactCreateNoCustomer(); break;
                        case "18": await SimulateD365CustomerAddressCreate(); break;
                        case "19": await SimulateD365CustomerAddressUpdate(); break;
                        case "20": await SimulateD365CustomerAddressDelete(); break;
                        case "21": await SimulateCaptureCustomerAddressCreate(); break;
                        case "22": await SimulateCaptureCustomerAddressUpdate(); break;
                        case "23": await SimulateCaptureCustomerAddressDelete(); break;
                        case "99": await ListenOnQueue(); break;
                        case "0": return 0;
                        default: Console.WriteLine("Invalid choice."); break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"ERROR: {ex.Message}");
                    Console.ResetColor();
                }

                Console.WriteLine();
            }
        }

        // ─── Flow 1: Simulate D365 FSC events ───────────────────────────

        /// <summary>
        /// Simulates a D365 FSC "OnExternalCreated" event for a customer entity.
        /// This is the same payload format that D365 Business Events sends to Service Bus.
        /// The Azure Function (QueueDataEvents) picks this up and routes it to Customer_To_Capture.
        /// </summary>
        static async Task SimulateD365CustomerCreate()
        {
            var eventContext = BuildD365Event(
                messageName: "OnExternalCreated",
                entityName: "mserp_custcustomerv3entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_partynumber"] = "000001234",
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_customeraccount"] = "CUST-SIM-001",
                    ["mserp_organizationname"] = "Simulation Test Corp",
                    ["mserp_namealias"] = "SimTest",
                    ["mserp_customergroupid"] = "RETAIL",
                    ["mserp_employeeresponsiblenumber"] = "ARM001",
                    ["mserp_blocked"] = "No",
                    ["mserp_depositrequired"] = "No",
                    ["mserp_dotnumber"] = "DOT123",
                    ["mserp_taxstatus"] = ""
                });

            await SendToQueue(D365Queue, eventContext, "D365 Customer Create Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalUpdated" event for a customer entity.
        /// Includes both target attributes and a changed-fields list.
        /// </summary>
        static async Task SimulateD365CustomerUpdate()
        {
            var changedFields = new List<string> { "mserp_organizationname", "mserp_namealias" };

            var eventContext = BuildD365Event(
                messageName: "OnExternalUpdated",
                entityName: "mserp_custcustomerv3entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_partynumber"] = "000001234",
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_customeraccount"] = "CUST-SIM-001",
                    ["mserp_organizationname"] = "Simulation Test Corp UPDATED",
                    ["mserp_namealias"] = "SimTestUpd",
                    ["mserp_customergroupid"] = "RETAIL",
                    ["mserp_employeeresponsiblenumber"] = "ARM001",
                    ["mserp_blocked"] = "No",
                    ["mserp_depositrequired"] = "No",
                    ["mserp_dotnumber"] = "DOT123",
                    ["mserp_taxstatus"] = ""
                },
                changedFields: changedFields);

            await SendToQueue(D365Queue, eventContext, "D365 Customer Update Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalDeleted" event for a customer entity.
        /// </summary>
        static async Task SimulateD365CustomerDelete()
        {
            Console.Write("  Enter CustomerAccount to delete [CUST-SIM-001]: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account)) account = "CUST-SIM-001";

            var eventContext = BuildD365Event(
                messageName: "OnExternalDeleted",
                entityName: "mserp_custcustomerv3entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_partynumber"] = "000001234",
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_customeraccount"] = account,
                });

            await SendToQueue(D365Queue, eventContext, "D365 Customer Delete Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalCreated" event for a contact entity.
        /// </summary>
        static async Task SimulateD365ContactCreate()
        {
            var eventContext = BuildD365Event(
                messageName: "OnExternalCreated",
                entityName: "mserp_smmcontactpersonv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_contactpersonid"] = "CONT-SIM-001",
                    ["mserp_firstname"] = "John",
                    ["mserp_lastname"] = "Doe",
                    ["mserp_middlename"] = "M",
                    ["mserp_associatedcontactnumber"] = "AR-038824",
                    ["mserp_associatedcontacttype"] = "Customer",
                    ["mserp_wtc_associatedcustomeraccount"] = "CUST-SIM-001",
                });

            await SendToQueue(D365Queue, eventContext, "D365 Contact Create Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalUpdated" event for a contact entity.
        /// </summary>
        static async Task SimulateD365ContactUpdate()
        {
            var changedFields = new List<string> { "mserp_firstname", "mserp_lastname" };

            var eventContext = BuildD365Event(
                messageName: "OnExternalUpdated",
                entityName: "mserp_smmcontactpersonv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_contactpersonid"] = "CONT-SIM-001",
                    ["mserp_firstname"] = "Jane",
                    ["mserp_lastname"] = "Smith",
                    ["mserp_middlename"] = "M",
                    ["mserp_associatedcontactnumber"] = "AR-038824",
                    ["mserp_associatedcontacttype"] = "Customer",
                    ["mserp_wtc_associatedcustomeraccount"] = "CUST-SIM-001",
                },
                changedFields: changedFields);

            await SendToQueue(D365Queue, eventContext, "D365 Contact Update Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalDeleted" event for a contact entity.
        /// </summary>
        static async Task SimulateD365ContactDelete()
        {
            Console.Write("  Enter ContactPersonId to delete [CONT-SIM-001]: ");
            var contactId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(contactId)) contactId = "CONT-SIM-001";

            var eventContext = BuildD365Event(
                messageName: "OnExternalDeleted",
                entityName: "mserp_smmcontactpersonv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_contactpersonid"] = contactId,
                    ["mserp_associatedcontactnumber"] = "AR-038824",
                });

            await SendToQueue(D365Queue, eventContext, "D365 Contact Delete Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 contact event WITHOUT mserp_wtc_associatedcustomeraccount.
        /// Expects the message to be silently discarded (no output on Capture queue).
        /// </summary>
        static async Task SimulateD365ContactCreateNoCustomer()
        {
            var eventContext = BuildD365Event(
                messageName: "OnExternalCreated",
                entityName: "mserp_smmcontactpersonv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"] = "WTC",
                    ["mserp_contactpersonid"] = "CONT-SIM-SKIP",
                    ["mserp_firstname"] = "Skip",
                    ["mserp_lastname"] = "Me",
                    // mserp_wtc_associatedcustomeraccount intentionally omitted
                });

            await SendToQueue(D365Queue, eventContext, "D365 Contact Create Event (no customer - expect skip)");
            Console.WriteLine("  → Listening for result on Capture output queue (should NOT receive anything)...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(15));
        }

        // ─── Flow 2: Simulate Capture messages ──────────────────────────

        /// <summary>
        /// Sends a CaptureCustomerMessageContract directly to the Capture customer queue.
        /// The Azure Function (QueueCaptureCustomer) picks this up and routes to Customer_To_D365FSC.
        /// </summary>
        static async Task SimulateCaptureCustomerCreate()
        {
            var message = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "New",
                Entity = "Customer",
                Data = new CaptureCustomerMessageContract
                {
                    DataAreaId = "WTC",
                    PartyType = "Organization",
                    OrganizationName = "Capture Simulation Customer",
                    NameAlias = "CapSimCust",
                    CustomerGroupId = "RETAIL",
                    EmployeeResponsibleNumber = "ARM001",
                    Blocked = "No",
                    DepositRequired = "No",
                    DotNumber = "DOT",
                    TaxStatus = ""
                }
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Customer Create");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture update message for an existing customer.
        /// </summary>
        static async Task SimulateCaptureCustomerUpdate()
        {
            Console.Write("  Enter CustomerAccount to update: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account))
            {
                Console.WriteLine("  CustomerAccount is required.");
                return;
            }

            Console.WriteLine("  Enter new values (press Enter to skip, no change):");

            Console.Write("    OrganizationName: ");
            var orgName = Console.ReadLine()?.Trim();

            Console.Write("    NameAlias: ");
            var nameAlias = Console.ReadLine()?.Trim();

            Console.Write("    CustomerGroupId: ");
            var customerGroup = Console.ReadLine()?.Trim();

            Console.Write("    EmployeeResponsibleNumber: ");
            var empNumber = Console.ReadLine()?.Trim();

            var data = new CaptureCustomerMessageContract
            {
                DataAreaId = "WTC",
                CustomerAccount = account,
            };

            if (!string.IsNullOrEmpty(orgName)) data.OrganizationName = orgName;
            if (!string.IsNullOrEmpty(nameAlias)) data.NameAlias = nameAlias;
            if (!string.IsNullOrEmpty(customerGroup)) data.CustomerGroupId = customerGroup;
            if (!string.IsNullOrEmpty(empNumber)) data.EmployeeResponsibleNumber = empNumber;

            var message = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
            {
                SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Update",
                Entity = "Customer",
                Data = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Customer Update");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture delete message for an existing customer.
        /// </summary>
        static async Task SimulateCaptureCustomerDelete()
        {
            Console.Write("  Enter CustomerAccount to delete: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account))
            {
                Console.WriteLine("  CustomerAccount is required.");
                return;
            }

            Console.Write("  Enter DataAreaId [WTC]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "WTC";

            var message = new CaptureMessageEnvelope<CaptureCustomerMessageContract>
            {
                SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Delete",
                Entity = "Customer",
                Data = new CaptureCustomerMessageContract
                {
                    DataAreaId = dataArea,
                    CustomerAccount = account,
                }
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Customer Delete");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        // ─── Flow 2b: Simulate Capture → D365 Contact ───────────────────

        /// <summary>
        /// Sends a CaptureContactMessageContract to create a new contact in D365.
        /// </summary>
        static async Task SimulateCaptureContactCreate()
        {
            Console.WriteLine("  Enter Contact details:");

            Console.Write("    FirstName [John]: ");
            var firstName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(firstName)) firstName = "John";

            Console.Write("    LastName [Doe]: ");
            var lastName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(lastName)) lastName = "Doe";

            Console.Write("    MiddleName []: ");
            var middleName = Console.ReadLine()?.Trim();

            Console.Write("    AssociatedPartyNumber [000005502]: ");
            var partyNumber = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(partyNumber)) partyNumber = "000005502";

            Console.Write("    DataAreaId [wtc]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "wtc";

            var data = new CaptureContactMessageContract
            {
                DataAreaId = dataArea,
                FirstName = firstName,
                LastName = lastName,
                AssociatedPartyNumber = partyNumber,
            };

            if (!string.IsNullOrEmpty(middleName)) data.MiddleName = middleName;

            var message = new CaptureMessageEnvelope<CaptureContactMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "New",
                Entity = "Contact",
                Data = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Contact Create");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture update message for an existing contact.
        /// </summary>
        static async Task SimulateCaptureContactUpdate()
        {
            Console.Write("  Enter ContactPersonId to update: ");
            var contactId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(contactId))
            {
                Console.WriteLine("  ContactPersonId is required.");
                return;
            }

            Console.WriteLine("  Enter new values (press Enter to skip, no change):");

            Console.Write("    FirstName: ");
            var firstName = Console.ReadLine()?.Trim();

            Console.Write("    LastName: ");
            var lastName = Console.ReadLine()?.Trim();

            Console.Write("    MiddleName: ");
            var middleName = Console.ReadLine()?.Trim();

            Console.Write("    AssociatedPartyNumber: ");
            var partyNumber = Console.ReadLine()?.Trim();

            Console.Write("    DataAreaId [wtc]: ");
            var dataAreaUpdate = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataAreaUpdate)) dataAreaUpdate = "wtc";

            var data = new CaptureContactMessageContract
            {
                DataAreaId = dataAreaUpdate,
                ContactPersonId = contactId,
            };

            if (!string.IsNullOrEmpty(firstName)) data.FirstName = firstName;
            if (!string.IsNullOrEmpty(lastName)) data.LastName = lastName;
            if (!string.IsNullOrEmpty(middleName)) data.MiddleName = middleName;
            if (!string.IsNullOrEmpty(partyNumber)) data.AssociatedPartyNumber = partyNumber;

            var message = new CaptureMessageEnvelope<CaptureContactMessageContract>
            {
                SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Update",
                Entity = "Contact",
                Data = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Contact Update");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture delete message for an existing contact.
        /// </summary>
        static async Task SimulateCaptureContactDelete()
        {
            Console.Write("  Enter ContactPersonId to delete: ");
            var contactId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(contactId))
            {
                Console.WriteLine("  ContactPersonId is required.");
                return;
            }

            Console.Write("  Enter DataAreaId [WTC]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "WTC";

            var message = new CaptureMessageEnvelope<CaptureContactMessageContract>
            {
                SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Delete",
                Entity = "Contact",
                Data = new CaptureContactMessageContract
                {
                    DataAreaId = dataArea,
                    ContactPersonId = contactId,
                }
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture Contact Delete");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        // ─── Flow 2c: Simulate Capture → D365 PartyContact ──────────────

        /// <summary>
        /// Sends a CapturePartyContactMessageContract to create a new party contact in D365.
        /// </summary>
        static async Task SimulateCapturePartyContactCreate()
        {
            Console.WriteLine("  Enter PartyContact details:");

            Console.Write("    Contact Party Number [000000706]: ");
            var contactPartyNumber = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(contactPartyNumber)) contactPartyNumber = "000000706";

            Console.Write("    Customer Party Number []: ");
            var customerPartyNumber = Console.ReadLine()?.Trim();

            Console.Write("    Locator [712-366-2241]: ");
            var locator = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(locator)) locator = "712-366-2241";

            Console.Write("    LocatorExtension []: ");
            var locatorExt = Console.ReadLine()?.Trim();

            Console.Write("    IsPrimary [Yes]: ");
            var isPrimary = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(isPrimary)) isPrimary = "Yes";

            Console.Write("    Description []: ");
            var description = Console.ReadLine()?.Trim();

            Console.Write("    Type [Phone]: ");
            var type = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(type)) type = "Phone";

            var data = new CapturePartyContactMessageContract
            {
                ContactPartyNumber = contactPartyNumber,
                Locator = locator,
                IsPrimary = isPrimary,
                Type = type,
            };

            if (!string.IsNullOrEmpty(locatorExt))     data.LocatorExtension    = locatorExt;
            if (!string.IsNullOrEmpty(description))    data.Description         = description;
            if (!string.IsNullOrEmpty(customerPartyNumber)) data.CustomerPartyNumber = customerPartyNumber;

            var message = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "New",
                Entity = "PartyContact",
                Data = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture PartyContact Create");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture update message for an existing party contact.
        /// </summary>
        static async Task SimulateCapturePartyContactUpdate()
        {
            Console.Write("  Enter Contact Party Number (or leave empty): ");
            var contactPartyNumber = Console.ReadLine()?.Trim();

            Console.Write("  Enter Customer Party Number (or leave empty): ");
            var customerPartyNumber = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(contactPartyNumber) && string.IsNullOrEmpty(customerPartyNumber))
            {
                Console.WriteLine("  At least one of ContactPartyNumber or CustomerPartyNumber is required.");
                return;
            }

            Console.Write("  Enter ElectronicAddressId: ");
            var electronicAddressId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(electronicAddressId))
            {
                Console.WriteLine("  ElectronicAddressId is required.");
                return;
            }

            Console.WriteLine("  Enter new values (press Enter to clear field, skip with space to leave unchanged):");

            Console.Write("    Locator [712-366-2241]: ");
            var locator = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(locator)) locator = "712-366-2241";

            Console.Write("    LocatorExtension []: ");
            var locatorExt = Console.ReadLine()?.Trim() ?? "";

            Console.Write("    IsPrimary [Yes]: ");
            var isPrimary = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(isPrimary)) isPrimary = "Yes";

            Console.Write("    Description []: ");
            var description = Console.ReadLine()?.Trim() ?? "";

            var data = new CapturePartyContactMessageContract
            {
                ContactPartyNumber  = string.IsNullOrEmpty(contactPartyNumber)  ? null : contactPartyNumber,
                CustomerPartyNumber = string.IsNullOrEmpty(customerPartyNumber) ? null : customerPartyNumber,
                ElectronicAddressId = electronicAddressId,
                Locator = locator,
                LocatorExtension = locatorExt,
                IsPrimary = isPrimary,
                Description = description,
            };

            var message = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
            {
                SourceId = $"SIM-UPD-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Update",
                Entity = "PartyContact",
                Data = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture PartyContact Update");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a Capture delete message for an existing party contact.
        /// </summary>
        static async Task SimulateCapturePartyContactDelete()
        {
            Console.Write("  Enter Contact Party Number (or leave empty): ");
            var contactPartyNumber = Console.ReadLine()?.Trim();

            Console.Write("  Enter Customer Party Number (or leave empty): ");
            var customerPartyNumber = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(contactPartyNumber) && string.IsNullOrEmpty(customerPartyNumber))
            {
                Console.WriteLine("  At least one of ContactPartyNumber or CustomerPartyNumber is required.");
                return;
            }

            Console.Write("  Enter ElectronicAddressId: ");
            var electronicAddressId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(electronicAddressId))
            {
                Console.WriteLine("  ElectronicAddressId is required.");
                return;
            }

            var message = new CaptureMessageEnvelope<CapturePartyContactMessageContract>
            {
                SourceId = $"SIM-DEL-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action = "Delete",
                Entity = "PartyContact",
                Data = new CapturePartyContactMessageContract
                {
                    ContactPartyNumber  = string.IsNullOrEmpty(contactPartyNumber)  ? null : contactPartyNumber,
                    CustomerPartyNumber = string.IsNullOrEmpty(customerPartyNumber) ? null : customerPartyNumber,
                    ElectronicAddressId = electronicAddressId,
                }
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture PartyContact Delete");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        // ─── Flow 1c: Simulate D365 FSC CustomerAddress events ─────────

        /// <summary>
        /// Simulates a D365 FSC "OnExternalCreated" event for a customer postal address entity.
        /// Routed to CustomerAddress_To_Capture orchestration via mserp_wtc_custpostaladdressv2entity.
        /// </summary>
        static async Task SimulateD365CustomerAddressCreate()
        {
            var eventContext = BuildD365Event(
                messageName: "OnExternalCreated",
                entityName: "mserp_wtc_custpostaladdressv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"]            = "wtc",
                    ["mserp_customeraccount"]       = "AR-019739",
                    ["mserp_isprimary"]             = "{\"Value\":200000001}",
                    ["mserp_addresslocationroles"]  = "Business",
                    ["mserp_addressdescription"]    = "Main Office",
                    ["mserp_addresscity"]           = "DALLAS",
                    ["mserp_addresszipcode"]        = "75201",
                    ["mserp_addressstreet"]         = "123 Main St",
                    ["mserp_addresscountryregionid"] = "USA",
                    ["mserp_addressstate"]          = "TX",
                });

            await SendToQueue(D365Queue, eventContext, "D365 CustomerAddress Create Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalUpdated" event for a customer postal address.
        /// Only changed fields are forwarded.
        /// </summary>
        static async Task SimulateD365CustomerAddressUpdate()
        {
            var changedFields = new List<string>
            {
                "mserp_customeraccount",
                "mserp_addresscity",
                "mserp_addressstreet",
                "mserp_addressdescription",
                "mserp_addresslocationroles",
                "mserp_isprimary",
            };

            var eventContext = BuildD365Event(
                messageName: "OnExternalUpdated",
                entityName: "mserp_wtc_custpostaladdressv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"]            = "wtc",
                    ["mserp_customeraccount"]       = "AR-019739",
                    ["mserp_addresslocationid"]     = "000005203",
                    ["mserp_isprimary"]             = "{\"Value\":200000001}",
                    ["mserp_addresslocationroles"]  = "Business;Delivery",
                    ["mserp_addressdescription"]    = "Main Office",
                    ["mserp_addresscity"]           = "DALLAS",
                    ["mserp_addresszipcode"]        = "75201",
                    ["mserp_addressstreet"]         = "456 Updated Ave",
                    ["mserp_addresscountryregionid"] = "USA",
                    ["mserp_addressstate"]          = "TX",
                },
                changedFields: changedFields);

            await SendToQueue(D365Queue, eventContext, "D365 CustomerAddress Update Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Simulates a D365 FSC "OnExternalDeleted" event for a customer postal address.
        /// </summary>
        static async Task SimulateD365CustomerAddressDelete()
        {
            var eventContext = BuildD365Event(
                messageName: "OnExternalDeleted",
                entityName: "mserp_wtc_custpostaladdressv2entity",
                attributes: new Dictionary<string, string>
                {
                    ["mserp_dataareaid"]        = "wtc",
                    ["mserp_customeraccount"]   = "AR-019739",
                    ["mserp_addresslocationid"] = "000005203",
                });

            await SendToQueue(D365Queue, eventContext, "D365 CustomerAddress Delete Event");
            Console.WriteLine("  → Listening for mapped result on Capture output queue...");
            await ListenForSingleMessage(CaptureOutputQueue, TimeSpan.FromSeconds(60));
        }

        // ─── Flow 2c: Simulate Capture → D365 CustomerAddress ────────────

        /// <summary>
        /// Sends a CaptureCustomerAddressMessageContract to create a new postal address in D365.
        /// </summary>
        static async Task SimulateCaptureCustomerAddressCreate()
        {
            Console.WriteLine("  Enter CustomerAddress details:");

            Console.Write("    DataAreaId [wtc]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "wtc";

            Console.Write("    CustomerAccount [AR-019739]: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account)) account = "AR-019739";

            Console.Write("    AddressDescription [Main Office]: ");
            var description = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(description)) description = "Main Office";

            Console.Write("    AddressStreet [123 Main St]: ");
            var street = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(street)) street = "123 Main St";

            Console.Write("    AddressCity [DALLAS]: ");
            var city = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(city)) city = "DALLAS";

            Console.Write("    AddressState [TX]: ");
            var state = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(state)) state = "TX";

            Console.Write("    AddressZipCode [75201]: ");
            var zip = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(zip)) zip = "75201";

            Console.Write("    AddressCountryRegionId [USA]: ");
            var country = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(country)) country = "USA";

            Console.Write("    AddressLocationRoles [Business]: ");
            var roles = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(roles)) roles = "Business";

            Console.Write("    IsPrimary [Yes]: ");
            var isPrimary = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(isPrimary)) isPrimary = "Yes";

            var data = new CaptureCustomerAddressMessageContract
            {
                DataAreaId             = dataArea,
                CustomerAccount        = account,
                AddressDescription     = description,
                AddressStreet          = street,
                AddressCity            = city,
                AddressState           = state,
                AddressZipCode         = zip,
                AddressCountryRegionId = country,
                AddressLocationRoles   = roles,
                IsPrimary              = isPrimary,
            };

            var message = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action   = "New",
                Entity   = "CustomerAddress",
                Data     = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture CustomerAddress Create");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a CaptureCustomerAddressMessageContract to update an existing postal address in D365.
        /// </summary>
        static async Task SimulateCaptureCustomerAddressUpdate()
        {
            Console.Write("  DataAreaId [wtc]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "wtc";

            Console.Write("  CustomerAccount: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account)) { Console.WriteLine("  CustomerAccount is required."); return; }

            Console.Write("  AddressLocationId: ");
            var locationId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(locationId)) { Console.WriteLine("  AddressLocationId is required."); return; }

            Console.WriteLine("  Enter new values (press Enter to skip):");

            Console.Write("    AddressStreet: ");
            var street = Console.ReadLine()?.Trim();

            Console.Write("    AddressCity: ");
            var city = Console.ReadLine()?.Trim();

            Console.Write("    AddressState: ");
            var state = Console.ReadLine()?.Trim();

            Console.Write("    AddressZipCode: ");
            var zip = Console.ReadLine()?.Trim();

            Console.Write("    AddressLocationRoles: ");
            var roles = Console.ReadLine()?.Trim();

            Console.Write("    IsPrimary (Yes/No): ");
            var isPrimary = Console.ReadLine()?.Trim();

            Console.Write("    AddressDescription: ");
            var description = Console.ReadLine()?.Trim();

            var data = new CaptureCustomerAddressMessageContract
            {
                DataAreaId        = dataArea,
                CustomerAccount   = account,
                AddressLocationId = locationId,
            };

            if (!string.IsNullOrEmpty(street))      data.AddressStreet          = street;
            if (!string.IsNullOrEmpty(city))        data.AddressCity            = city;
            if (!string.IsNullOrEmpty(state))       data.AddressState           = state;
            if (!string.IsNullOrEmpty(zip))         data.AddressZipCode         = zip;
            if (!string.IsNullOrEmpty(roles))       data.AddressLocationRoles   = roles;
            if (!string.IsNullOrEmpty(isPrimary))   data.IsPrimary              = isPrimary;
            if (!string.IsNullOrEmpty(description)) data.AddressDescription     = description;

            var message = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action   = "Update",
                Entity   = "CustomerAddress",
                Data     = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture CustomerAddress Update");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Sends a CaptureCustomerAddressMessageContract to delete a postal address from D365.
        /// Only the 3 key fields are required.
        /// </summary>
        static async Task SimulateCaptureCustomerAddressDelete()
        {
            Console.Write("  DataAreaId [wtc]: ");
            var dataArea = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dataArea)) dataArea = "wtc";

            Console.Write("  CustomerAccount: ");
            var account = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(account)) { Console.WriteLine("  CustomerAccount is required."); return; }

            Console.Write("  AddressLocationId: ");
            var locationId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(locationId)) { Console.WriteLine("  AddressLocationId is required."); return; }

            var data = new CaptureCustomerAddressMessageContract
            {
                DataAreaId        = dataArea,
                CustomerAccount   = account,
                AddressLocationId = locationId,
            };

            var message = new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
            {
                SourceId = $"SIM-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Action   = "Delete",
                Entity   = "CustomerAddress",
                Data     = data
            };

            await SendToQueue(CaptureCustomerQueue, message, "Capture CustomerAddress Delete");
            Console.WriteLine("  → Listening for response on response queue...");
            await ListenForSingleMessage(CaptureCustomerResponseQueue, TimeSpan.FromSeconds(60));
        }

        // ─── Queue Listener ─────────────────────────────────────────────

        /// <summary>
        /// Interactively listens on a chosen queue and prints received messages.
        /// </summary>
        static async Task ListenOnQueue()
        {
            Console.WriteLine("  Available queues:");
            Console.WriteLine("    1. bat-d365fsc-to-capture (D365 → Capture output)");
            Console.WriteLine("    2. bat-capture-to-d365fsc-response (Capture → D365 responses)");
            Console.Write("  Queue choice: ");

            var input = Console.ReadLine()?.Trim();
            var queueName = input switch
            {
                "1" => CaptureOutputQueue,
                "2" => CaptureCustomerResponseQueue,
                _ => null
            };

            if (string.IsNullOrEmpty(queueName))
            {
                Console.WriteLine("  Invalid choice.");
                return;
            }

            Console.WriteLine($"  Listening on '{queueName}' (press Ctrl+C to stop)...");
            Console.WriteLine();

            await using var client = new ServiceBusClient(ServiceBusConnectionString);
            await using var receiver = client.CreateReceiver(queueName);

            while (true)
            {
                var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
                if (msg is null)
                {
                    Console.Write(".");
                    continue;
                }

                PrintMessage(msg);
                await receiver.CompleteMessageAsync(msg);
            }
        }

        /// <summary>
        /// Listens for a single response message with a timeout.
        /// </summary>
        static async Task ListenForSingleMessage(string queueName, TimeSpan timeout)
        {
            await using var client = new ServiceBusClient(ServiceBusConnectionString);
            await using var receiver = client.CreateReceiver(queueName);

            var msg = await receiver.ReceiveMessageAsync(timeout);
            if (msg is null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⏱ No response received within {timeout.TotalSeconds}s on '{queueName}'.");
                Console.ResetColor();
                return;
            }

            PrintMessage(msg);
            await receiver.CompleteMessageAsync(msg);
        }

        // ─── Helper Methods ─────────────────────────────────────────────

        /// <summary>
        /// Builds a simulated D365 FSC EventContext JSON payload matching the format
        /// that D365 Business Events produces.
        /// </summary>
        static object BuildD365Event(
            string messageName,
            string entityName,
            Dictionary<string, string> attributes,
            List<string>? changedFields = null)
        {
            // Build the InputParameters structure matching D365 format
            var inputParams = new List<object>();

            // "Target" parameter — contains the entity attributes
            var entityAttributes = attributes.Select(kvp => new
            {
                key = kvp.Key,
                value = kvp.Value
            }).ToList<object>();

            inputParams.Add(new
            {
                key = "Target",
                value = new
                {
                    Attributes = entityAttributes,
                    Id = Guid.NewGuid().ToString(),
                    LogicalName = entityName
                }
            });

            // "ChangedFields" parameter — only for update events
            if (changedFields is not null)
            {
                inputParams.Add(new
                {
                    key = "ChangedFields",
                    value = changedFields  // must be a JSON array, not comma-separated string
                });
            }

            return new
            {
                MessageName = messageName,
                PrimaryEntityName = entityName,
                UserAzureActiveDirectoryObjectId = "00000000-0000-0000-0000-000000000000",
                UserId = "SimulationUser",
                InputParameters = inputParams,
                OperationCreatedOn = DateTime.UtcNow.ToString("o"),
                OperationId = Guid.NewGuid().ToString()
            };
        }

        /// <summary>
        /// Serializes and sends a message to the specified Service Bus queue.
        /// </summary>
        static async Task SendToQueue(string queueName, object message, string description)
        {
            var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = false });

            await using var client = new ServiceBusClient(ServiceBusConnectionString);
            await using var sender = client.CreateSender(queueName);

            var sbMessage = new ServiceBusMessage(payload)
            {
                ContentType = "application/json",
                MessageId = $"SIM-{Guid.NewGuid():N}"
            };

            await sender.SendMessageAsync(sbMessage);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ Sent [{description}] to '{queueName}'");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    MessageId: {sbMessage.MessageId}");
            Console.WriteLine($"    Payload:   {payload}");
            Console.ResetColor();
        }

        /// <summary>
        /// Prints a received Service Bus message to the console.
        /// </summary>
        static void PrintMessage(ServiceBusReceivedMessage msg)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  ← Received message:");
            Console.ResetColor();
            Console.WriteLine($"    MessageId:    {msg.MessageId}");
            Console.WriteLine($"    EnqueuedTime: {msg.EnqueuedTime}");

            var body = msg.Body.ToString();
            try
            {
                // Pretty-print JSON
                var parsed = JsonSerializer.Deserialize<JsonElement>(body);
                body = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { /* not JSON, print raw */ }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    Body:\n{body}");
            Console.ResetColor();
        }
    }
}
