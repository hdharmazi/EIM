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

            // Try plain integer string first (e.g. "200000001")
            if (int.TryParse(rawValue, out var code))
                return YesNoMap.TryGetValue(code, out var label) ? label : rawValue;

            // Try JSON object with Value property (e.g. {"Value":200000001,...})
            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                if (doc.RootElement.TryGetProperty("Value", out var intVal)
                    && intVal.TryGetInt32(out var jsonCode))
                    return YesNoMap.TryGetValue(jsonCode, out var jsonLabel) ? jsonLabel : jsonCode.ToString();
            }
            catch { }

            return rawValue;
        }


        protected override CaptureCustomerAddressMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            return new CaptureCustomerAddressMessageContract
            {
                DataAreaId             = getField("mserp_dataareaid", true),
                CustomerAccount        = getField("mserp_customeraccount", true),
                AddressLocationId      = getField("mserp_addresslocationid", true),
                IsPrimary              = ResolveYesNo(getField("mserp_isprimary", true)),
                AddressLocationRoles   = getField("mserp_addresslocationroles", true),
                AddressDescription     = getField("mserp_addressdescription", true),
                AddressCity            = getField("mserp_addresscity", true),
                AddressZipCode         = getField("mserp_addresszipcode", true),
                AddressStreet          = getField("mserp_addressstreet", true),
                AddressCountryRegionId = getField("mserp_addresscountryregionid", true),
                AddressState           = getField("mserp_addressstate", true),
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
