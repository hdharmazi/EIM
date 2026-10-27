using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// D365 FSC → Capture: Maps customer address data events to CaptureCustomerAddressMessageContract.
    /// Source entity: mserp_wtc_custpostaladdressv2entity
    /// <para>
    /// D365 versions postal addresses: editing one creates a new date-effective
    /// <c>LogisticsPostalAddress</c> row (new RecId, new ValidFrom) for the same location id, so
    /// Dataverse raises <c>OnExternalCreated</c> for an edit. The entity's computed
    /// <c>mserp_wtc_haspriorversion</c> marker is Yes when an earlier version of the location's
    /// address exists; such a creation is forwarded as <c>Update</c> with the full address. When the
    /// marker is absent or not Yes the action stays <c>New</c>.
    /// </para>
    /// </summary>
    public class CustomerAddress_To_Capture : D365EventForwardOrchestration<CaptureCustomerAddressMessageContract>
    {
        internal const string PriorVersionField = "mserp_wtc_haspriorversion";

        protected override string EntityName => "CustomerAddress";

        private static readonly Dictionary<int, string> YesNoMap = new()
        {
            [200000001] = "Yes",
            [200000000] = "No",
        };

        protected static string? ResolveYesNo(string? rawValue)
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

        /// <summary>
        /// A creation event for an address that supersedes a prior version is an update for Capture.
        /// Other actions are unchanged; a forced action (the delete subclass) is applied afterwards.
        /// </summary>
        protected override string ResolveAction(string derivedAction, EventContext context,
            IReadOnlyDictionary<string, object> targetAttributes)
        {
            if (derivedAction != "New") return derivedAction;
            targetAttributes.TryGetValue(PriorVersionField, out var raw);
            return ResolveYesNo(raw?.ToString()) == "Yes" ? "Update" : derivedAction;
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
