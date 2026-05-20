using ARM.Integration.Core.Model;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration
{
    /// <summary>
    /// D365 FSC → Capture: Maps customer address data events to CaptureCustomerAddressMessageContract.
    /// Source entity: mserp_wtc_custpostaladdressv2entity
    /// </summary>
    public class CustomerAddress_To_Capture : D365EventForwardOrchestration<CaptureCustomerAddressMessageContract>
    {
        protected override string EntityName => "CustomerAddress";

        private static readonly Dictionary<int, string> YesNoMap = new()
        {
            [200000001] = "Yes",
            [200000000] = "No",
        };

        private static string? ResolveYesNo(string? rawValue)
        {
            if (rawValue is null) return null;

            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                if (doc.RootElement.TryGetProperty("Value", out var intVal)
                    && intVal.TryGetInt32(out var code))
                {
                    return YesNoMap.TryGetValue(code, out var label) ? label : code.ToString();
                }
            }
            catch
            {
                // Not JSON — return as-is
            }

            return rawValue;
        }

        protected override CaptureCustomerAddressMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            return new CaptureCustomerAddressMessageContract
            {
                CustomerLegalEntityId  = getField("mserp_dataareaid", true),
                CustomerAccountNumber  = getField("mserp_customeraccount", true),
                AddressLocationId      = getField("mserp_addresslocationid", true),
                IsPrimary              = ResolveYesNo(getField("mserp_isprimary", false)),
                AddressLocationRoles   = getField("mserp_addresslocationroles", false),
                AddressDescription     = getField("mserp_addressdescription", false),
                // mserp_wtc_custpostaladdressv2entity is exclusively a postal address entity —
                // there is no source field for this; always "Yes".
                IsPostalAddress        = "Yes",
                AddressCity            = getField("mserp_addresscity", false),
                AddressZipCode         = getField("mserp_addresszipcode", false),
                AddressStreet          = getField("mserp_addressstreet", false),
                AddressCountryRegionId = getField("mserp_addresscountryregionid", false),
                AddressState           = getField("mserp_addressstate", false),
            };
        }

        protected override object CreateEnvelope(CaptureCustomerAddressMessageContract data, string sourceId, string action, string? createdDateTime)
        {
            return new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
            {
                SourceId        = sourceId,
                Action          = action,
                Entity          = EntityName,
                CreatedDateTime = createdDateTime,
                Data            = data,
            };
        }
    }
}
