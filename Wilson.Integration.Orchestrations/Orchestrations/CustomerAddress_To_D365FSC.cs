using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// Capture -> D365 FSC: Maps CaptureCustomerAddressMessageContract to WTCCustomerPostalAddressesV2 entity CRUD.
    /// Keys: dataAreaId, CustomerAccount, AddressLocationId
    /// </summary>
    public class CustomerAddress_To_D365FSC : D365CrudOrchestration<CaptureCustomerAddressMessageContract, CustomerPostalAddressesEntity>
    {
        protected override string D365EntityName => "WTCCustomerPostalAddressesV2";
        protected override string ResponseEntityName => "CustomerAddress";

        protected override (string? SourceId, string? Action, CaptureCustomerAddressMessageContract Data) DeserializePayload(string payload)
        {
            var envelope = JsonSerializer.Deserialize<CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>>(payload)
                ?? throw new JsonException("Envelope is null.");
            return (envelope.SourceId, envelope.Action, envelope.Data ?? throw new JsonException("Data is null."));
        }

        protected override CustomerPostalAddressesEntity MapToTarget(CaptureCustomerAddressMessageContract source)
        {
            return new CustomerPostalAddressesEntity
            {
                DataAreaId             = source.DataAreaId,
                CustomerAccount        = source.CustomerAccount,
                AddressLocationId      = source.AddressLocationId,
                IsPrimary              = source.IsPrimary,
                AddressLocationRoles   = source.AddressLocationRoles,
                AddressDescription     = source.AddressDescription,
                AddressCity            = source.AddressCity,
                AddressZipCode         = source.AddressZipCode,
                AddressStreet          = source.AddressStreet,
                AddressCountryRegionId = source.AddressCountryRegionId,
                AddressState           = source.AddressState,
            };
        }

        protected override EntityKey BuildEntityKey(CustomerPostalAddressesEntity d365Entity)
        {
            return EntityKey.Create("dataAreaId", d365Entity.DataAreaId)
                .Add("CustomerAccount", d365Entity.CustomerAccount)
                .Add("AddressLocationId", d365Entity.AddressLocationId);
        }

        protected override CaptureCustomerAddressMessageContract BuildResponseData(
            CaptureCustomerAddressMessageContract source, CustomerPostalAddressesEntity? d365Result, string action)
        {
            if (d365Result != null && action.Equals("New", StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureCustomerAddressMessageContract
                {
                    DataAreaId        = d365Result.DataAreaId,
                    CustomerAccount   = d365Result.CustomerAccount,
                    AddressLocationId = d365Result.AddressLocationId,
                };
            }

            return new CaptureCustomerAddressMessageContract
            {
                DataAreaId        = source.DataAreaId,
                CustomerAccount   = source.CustomerAccount,
                AddressLocationId = source.AddressLocationId,
            };
        }
    }
}
