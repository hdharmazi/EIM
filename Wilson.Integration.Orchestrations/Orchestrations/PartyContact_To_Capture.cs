using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// D365 FSC → Capture: Maps party contact (electronic address) data events to CapturePartyContactMessageContract.
    /// </summary>
    public class PartyContact_To_Capture : D365EventForwardOrchestration<CapturePartyContactMessageContract>
    {
        protected override string EntityName => "PartyContact";

        private static readonly Dictionary<int, string> TypeMap = new()
        {
            [200000000] = "None",
            [200000001] = "Phone",
            [200000002] = "Email",
            [200000003] = "URL",
            [200000004] = "Telex",
            [200000005] = "Fax",
            [200000006] = "Facebook",
            [200000007] = "Twitter",
            [200000008] = "LinkedIn",
        };

        private static string? ResolveType(string? rawValue)
        {
            if (rawValue is null) return null;

            // Try plain integer string first (e.g. "200000001")
            if (int.TryParse(rawValue, out var code))
                return TypeMap.TryGetValue(code, out var label) ? label : rawValue;

            // Try JSON object with Value property (e.g. {"Value":200000001,...})
            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                if (doc.RootElement.TryGetProperty("Value", out var intVal)
                    && intVal.TryGetInt32(out var jsonCode))
                    return TypeMap.TryGetValue(jsonCode, out var jsonLabel) ? jsonLabel : jsonCode.ToString();
            }
            catch { }

            return rawValue;
        }

        protected override CapturePartyContactMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            var partyNumber    = getField("mserp_partynumber",        true);
            var isContact      = !string.IsNullOrWhiteSpace(getField("mserp_wtc_contactperson", false));
            var isCustomer     = !string.IsNullOrWhiteSpace(getField("mserp_wtc_custaccount",   false));
            return new CapturePartyContactMessageContract
            {
                ContactPartyNumber  = isContact  ? partyNumber : "",
                CustomerPartyNumber = isCustomer ? partyNumber : "",
                ElectronicAddressId = getField("mserp_electronicaddressid", true),
                Locator = getField("mserp_locator", true),
                LocatorExtension = getField("mserp_locatorextension", true),
                Description = getField("mserp_description", true),
                Type = ResolveType(getField("mserp_type", true)),
            };
        }

        protected override object CreateEnvelope(CapturePartyContactMessageContract data, string sourceId, string action, string? createdDateTime)
        {
            return new CaptureMessageEnvelope<CapturePartyContactMessageContract>
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
