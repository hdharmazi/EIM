using ARM.Integration.EIM;
using ARM.Integration.EIM.Utilities;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// D365 FSC → Capture: forwards customer address soft-deletes as <c>Action = "Delete"</c>.
    /// Source entity: mserp_wtc_custpostaladdressdeletedv2entity
    /// </summary>
    // Inherited MapFields assumes this entity exposes the same attributes as mserp_wtc_custpostaladdressv2entity.
    public class CustomerAddressDelete_To_Capture : CustomerAddress_To_Capture
    {
        private const string IsDeletedField = "mserp_wtc_isdeleted";

        protected override string? ForcedAction => "Delete";

        protected override string? GetSkipReason(EventContext context)
        {
            var attrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
            object? raw = null;
            attrs?.TryGetValue(IsDeletedField, out raw);
            var observed = raw?.ToString();

            return ResolveYesNo(observed) == "Yes"
                ? null
                : $"{IsDeletedField} is '{observed ?? "<missing>"}', expected 200000001 (Yes)";
        }
    }
}
