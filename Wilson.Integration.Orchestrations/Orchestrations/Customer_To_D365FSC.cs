using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// Capture → D365 FSC: Maps CaptureCustomerMessageContract to CustomersV3 entity CRUD.
    /// </summary>
    public class Customer_To_D365FSC : D365CrudOrchestration<CaptureCustomerMessageContract, CustomersEntity>
    {
        protected override string D365EntityName => "CustomersV3";
        protected override string ResponseEntityName => "Customer";

        protected override (string? SourceId, string? Action, CaptureCustomerMessageContract Data) DeserializePayload(string payload)
        {
            var envelope = JsonSerializer.Deserialize<CaptureMessageEnvelope<CaptureCustomerMessageContract>>(payload)
                ?? throw new JsonException("Envelope is null.");
            return (envelope.SourceId, envelope.Action, envelope.Data ?? throw new JsonException("Data is null."));
        }

        protected override CustomersEntity MapToTarget(CaptureCustomerMessageContract source)
        {
            return new CustomersEntity
            {
                DataAreaId = source.DataAreaId,
                CustomerAccount = source.CustomerAccount,
                OrganizationName = source.OrganizationName,
                NameAlias = source.NameAlias,
                CustomerGroupId = source.CustomerGroupId,
                EmployeeResponsibleNumber = source.EmployeeResponsibleNumber,
                //Blocked = source.Blocked,
                //DepositRequired = source.DepositRequired,
                //DotNumber = source.DotNumber,
                //TaxStatus = source.TaxStatus,
                PartyNumber = source.PartyNumber,
                SalesCurrencyCode = "USD",
            };
        }

        protected override EntityKey BuildEntityKey(CustomersEntity d365Entity)
        {
            return EntityKey.Create("dataAreaId", d365Entity.DataAreaId)
                .Add("CustomerAccount", d365Entity.CustomerAccount);
        }

        protected override CaptureCustomerMessageContract BuildResponseData(
            CaptureCustomerMessageContract source, CustomersEntity? d365Result, string action)
        {
            if (d365Result != null && action.Equals("New", StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureCustomerMessageContract
                {
                    PartyNumber = d365Result.PartyNumber,
                    CustomerAccount = d365Result.CustomerAccount,
                    DataAreaId = d365Result.DataAreaId,
                    OrganizationName = d365Result.OrganizationName,
                    NameAlias = d365Result.NameAlias,
                    CustomerGroupId = d365Result.CustomerGroupId,
                    EmployeeResponsibleNumber = d365Result.EmployeeResponsibleNumber,
                    Blocked = d365Result.Blocked,
                    DepositRequired = d365Result.DepositRequired,
                };
            }

            return new CaptureCustomerMessageContract
            {
                PartyNumber = source.PartyNumber,
                CustomerAccount = source.CustomerAccount,
            };
        }
    }
}
