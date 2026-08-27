using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// D365 FSC → Capture: Maps ContactPersonV2Entity data events to CaptureContactMessageContract.
    /// </summary>
    public class Contact_To_Capture : D365EventForwardOrchestration<CaptureContactMessageContract>
    {
        protected override string EntityName => "Contact";

        private static readonly Dictionary<int, string> AssociatedContactTypeMap = new()
        {
            [200000000] = "Customer",
            [200000001] = "Vendor",
            [200000002] = "Competitor",
            [200000003] = "Worker",
        };

        private static string? ResolveOptionSet(string? rawValue)
        {
            if (rawValue is null) return null;

            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                if (doc.RootElement.TryGetProperty("Value", out var intVal)
                    && intVal.TryGetInt32(out var code))
                {
                    return AssociatedContactTypeMap.TryGetValue(code, out var label) ? label : code.ToString();
                }
            }
            catch
            {
                // Not JSON, return as-is
            }

            return rawValue;
        }

        protected override CaptureContactMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            return new CaptureContactMessageContract
            {
                DataAreaId = getField("mserp_dataareaid", false),
                ContactPersonId = getField("mserp_contactpersonid", true),
                FirstName = getField("mserp_firstname", false),
                LastName = getField("mserp_lastname", false),
                MiddleName = getField("mserp_middlename", false),
                AssociatedCustomerAccount = getField("mserp_wtc_associatedcustomeraccount", true),
                AssociatedPartyNumber = getField("mserp_associatedpartynumber", true),
                ContactPersonPartyNumber = getField("mserp_contactpersonpartynumber", true)

            };
        }

        protected override object CreateEnvelope(CaptureContactMessageContract data, string sourceId, string action, string? createdDateTime)
        {
            return new CaptureMessageEnvelope<CaptureContactMessageContract>
            {
                SourceId = sourceId,
                Action = action,
                Entity = EntityName,
                CreatedDateTime = createdDateTime,
                Data = data,
            };
        }
    }
}
