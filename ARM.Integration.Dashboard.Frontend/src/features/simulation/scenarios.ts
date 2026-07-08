export interface ScenarioField {
  k: string;
  l: string;
  d: string;
  req?: boolean;
}

export interface Scenario {
  label: string;
  group: string;
  fields: ScenarioField[];
  custom?: "salesorder-import";
}

// Ported from the legacy Simulation/Index.cshtml scenario definitions.
export const SCENARIOS: Record<string, Scenario> = {
  "d365-customer-create": {
    label: "D365 → Customer Create",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_customeraccount", l: "Customer Account", d: "CUST-SIM-001" },
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_partynumber", l: "Party Number", d: "000001234" },
      { k: "mserp_organizationname", l: "Organization Name", d: "Simulation Test Corp" },
      { k: "mserp_namealias", l: "Name Alias", d: "SimTest" },
      { k: "mserp_customergroupid", l: "Customer Group ID", d: "RETAIL" },
      { k: "mserp_employeeresponsiblenumber", l: "Employee Responsible No.", d: "ARM001" },
    ],
  },
  "d365-customer-update": {
    label: "D365 → Customer Update",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_customeraccount", l: "Customer Account", d: "CUST-SIM-001" },
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_partynumber", l: "Party Number", d: "000001234" },
      { k: "mserp_organizationname", l: "Organization Name", d: "Simulation Test Corp UPDATED" },
      { k: "mserp_namealias", l: "Name Alias", d: "SimTestUpd" },
      { k: "mserp_customergroupid", l: "Customer Group ID", d: "RETAIL" },
      { k: "mserp_employeeresponsiblenumber", l: "Employee Responsible No.", d: "ARM001" },
    ],
  },
  "d365-customer-delete": {
    label: "D365 → Customer Delete",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_customeraccount", l: "Customer Account", d: "CUST-SIM-001", req: true },
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_partynumber", l: "Party Number", d: "000001234" },
    ],
  },
  "d365-contact-create": {
    label: "D365 → Contact Create",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_contactpersonid", l: "Contact Person ID", d: "CONT-SIM-001" },
      { k: "mserp_firstname", l: "First Name", d: "John" },
      { k: "mserp_lastname", l: "Last Name", d: "Doe" },
      { k: "mserp_middlename", l: "Middle Name", d: "M" },
      { k: "mserp_associatedcontactnumber", l: "Associated Contact Number", d: "AR-038824" },
      { k: "mserp_associatedcontacttype", l: "Associated Contact Type", d: "Customer" },
      { k: "mserp_wtc_associatedcustomeraccount", l: "Associated Customer Account", d: "CUST-SIM-001" },
    ],
  },
  "d365-contact-update": {
    label: "D365 → Contact Update",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_contactpersonid", l: "Contact Person ID", d: "CONT-SIM-001" },
      { k: "mserp_firstname", l: "First Name", d: "Jane" },
      { k: "mserp_lastname", l: "Last Name", d: "Smith" },
      { k: "mserp_middlename", l: "Middle Name", d: "M" },
      { k: "mserp_associatedcontactnumber", l: "Associated Contact Number", d: "AR-038824" },
      { k: "mserp_associatedcontacttype", l: "Associated Contact Type", d: "Customer" },
      { k: "mserp_wtc_associatedcustomeraccount", l: "Associated Customer Account", d: "CUST-SIM-001" },
    ],
  },
  "d365-contact-delete": {
    label: "D365 → Contact Delete",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_contactpersonid", l: "Contact Person ID", d: "CONT-SIM-001", req: true },
    ],
  },
  "d365-contact-create-no-customer": {
    label: "D365 → Contact Create (no customer – skip)",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "WTC" },
      { k: "mserp_contactpersonid", l: "Contact Person ID", d: "CONT-SIM-SKIP" },
      { k: "mserp_firstname", l: "First Name", d: "Skip" },
      { k: "mserp_lastname", l: "Last Name", d: "Me" },
    ],
  },
  "d365-address-create": {
    label: "D365 → CustomerAddress Create",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "wtc" },
      { k: "mserp_customeraccount", l: "Customer Account", d: "AR-019739" },
      { k: "mserp_addressstreet", l: "Street", d: "123 Main St" },
      { k: "mserp_addresscity", l: "City", d: "DALLAS" },
      { k: "mserp_addressstate", l: "State", d: "TX" },
      { k: "mserp_addresszipcode", l: "Zip Code", d: "75201" },
      { k: "mserp_addresscountryregionid", l: "Country Region ID", d: "USA" },
      { k: "mserp_addresslocationroles", l: "Location Roles", d: "Business" },
      { k: "mserp_addressdescription", l: "Description", d: "Main Office" },
    ],
  },
  "d365-address-update": {
    label: "D365 → CustomerAddress Update",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "wtc" },
      { k: "mserp_customeraccount", l: "Customer Account", d: "AR-019739" },
      { k: "mserp_addresslocationid", l: "Location ID", d: "000005203" },
      { k: "mserp_addressstreet", l: "Street", d: "456 Updated Ave" },
      { k: "mserp_addresscity", l: "City", d: "DALLAS" },
      { k: "mserp_addressstate", l: "State", d: "TX" },
      { k: "mserp_addresszipcode", l: "Zip Code", d: "75201" },
      { k: "mserp_addresscountryregionid", l: "Country Region ID", d: "USA" },
      { k: "mserp_addresslocationroles", l: "Location Roles", d: "Business;Delivery" },
      { k: "mserp_addressdescription", l: "Description", d: "Main Office" },
    ],
  },
  "d365-address-delete": {
    label: "D365 → CustomerAddress Delete",
    group: "D365 → Capture",
    fields: [
      { k: "mserp_dataareaid", l: "Data Area ID", d: "wtc" },
      { k: "mserp_customeraccount", l: "Customer Account", d: "AR-019739", req: true },
      { k: "mserp_addresslocationid", l: "Location ID", d: "000005203", req: true },
    ],
  },
  "capture-customer-create": {
    label: "Capture → Customer Create",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "WTC" },
      { k: "organizationname", l: "Organization Name", d: "Capture Simulation Customer" },
      { k: "namealias", l: "Name Alias", d: "CapSimCust" },
      { k: "customergroupid", l: "Customer Group ID", d: "RETAIL" },
      { k: "employeeresponsiblenumber", l: "Employee Responsible No.", d: "ARM001" },
    ],
  },
  "capture-customer-update": {
    label: "Capture → Customer Update",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "WTC" },
      { k: "customeraccount", l: "Customer Account", d: "", req: true },
      { k: "organizationname", l: "Organization Name", d: "" },
      { k: "namealias", l: "Name Alias", d: "" },
      { k: "customergroupid", l: "Customer Group ID", d: "" },
      { k: "employeeresponsiblenumber", l: "Employee Responsible No.", d: "" },
    ],
  },
  "capture-customer-delete": {
    label: "Capture → Customer Delete",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "WTC" },
      { k: "customeraccount", l: "Customer Account", d: "", req: true },
    ],
  },
  "capture-contact-create": {
    label: "Capture → Contact Create",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "wtc" },
      { k: "firstname", l: "First Name", d: "John" },
      { k: "lastname", l: "Last Name", d: "Doe" },
      { k: "middlename", l: "Middle Name", d: "" },
      { k: "associatedpartynumber", l: "Associated Party Number", d: "000005502" },
    ],
  },
  "capture-contact-update": {
    label: "Capture → Contact Update",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "wtc" },
      { k: "contactpersonid", l: "Contact Person ID", d: "", req: true },
      { k: "firstname", l: "First Name", d: "" },
      { k: "lastname", l: "Last Name", d: "" },
      { k: "middlename", l: "Middle Name", d: "" },
      { k: "associatedpartynumber", l: "Associated Party Number", d: "" },
    ],
  },
  "capture-contact-delete": {
    label: "Capture → Contact Delete",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "WTC" },
      { k: "contactpersonid", l: "Contact Person ID", d: "", req: true },
    ],
  },
  "capture-partycontact-create": {
    label: "Capture → PartyContact Create",
    group: "Capture → D365",
    fields: [
      { k: "contactpartynumber", l: "Contact Party Number", d: "000000706" },
      { k: "customerpartynumber", l: "Customer Party Number", d: "" },
      { k: "locator", l: "Locator", d: "712-366-2241" },
      { k: "locatorextension", l: "Locator Extension", d: "" },
      { k: "isprimary", l: "Is Primary", d: "Yes" },
      { k: "description", l: "Description", d: "" },
      { k: "type", l: "Type", d: "Phone" },
    ],
  },
  "capture-partycontact-update": {
    label: "Capture → PartyContact Update",
    group: "Capture → D365",
    fields: [
      { k: "contactpartynumber", l: "Contact Party Number", d: "" },
      { k: "customerpartynumber", l: "Customer Party Number", d: "" },
      { k: "electronicaddressid", l: "Electronic Address ID", d: "", req: true },
      { k: "locator", l: "Locator", d: "712-366-2241" },
      { k: "locatorextension", l: "Locator Extension", d: "" },
      { k: "isprimary", l: "Is Primary", d: "Yes" },
      { k: "description", l: "Description", d: "" },
    ],
  },
  "capture-partycontact-delete": {
    label: "Capture → PartyContact Delete",
    group: "Capture → D365",
    fields: [
      { k: "contactpartynumber", l: "Contact Party Number", d: "" },
      { k: "customerpartynumber", l: "Customer Party Number", d: "" },
      { k: "electronicaddressid", l: "Electronic Address ID", d: "", req: true },
    ],
  },
  "capture-address-create": {
    label: "Capture → CustomerAddress Create",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "wtc" },
      { k: "customeraccount", l: "Customer Account", d: "AR-019739" },
      { k: "addressdescription", l: "Description", d: "Main Office" },
      { k: "addressstreet", l: "Street", d: "123 Main St" },
      { k: "addresscity", l: "City", d: "DALLAS" },
      { k: "addressstate", l: "State", d: "TX" },
      { k: "addresszipcode", l: "Zip Code", d: "75201" },
      { k: "addresscountryregionid", l: "Country Region ID", d: "USA" },
      { k: "addresslocationroles", l: "Location Roles", d: "Business" },
      { k: "isprimary", l: "Is Primary", d: "Yes" },
    ],
  },
  "capture-address-update": {
    label: "Capture → CustomerAddress Update",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "wtc" },
      { k: "customeraccount", l: "Customer Account", d: "", req: true },
      { k: "addresslocationid", l: "Location ID", d: "", req: true },
      { k: "addressstreet", l: "Street", d: "" },
      { k: "addresscity", l: "City", d: "" },
      { k: "addressstate", l: "State", d: "" },
      { k: "addresszipcode", l: "Zip Code", d: "" },
      { k: "addresslocationroles", l: "Location Roles", d: "" },
      { k: "isprimary", l: "Is Primary", d: "" },
      { k: "addressdescription", l: "Description", d: "" },
    ],
  },
  "capture-address-delete": {
    label: "Capture → CustomerAddress Delete",
    group: "Capture → D365",
    fields: [
      { k: "dataAreaId", l: "Data Area ID", d: "wtc" },
      { k: "customeraccount", l: "Customer Account", d: "", req: true },
      { k: "addresslocationid", l: "Location ID", d: "", req: true },
    ],
  },
  "capture-salesorder-import": {
    label: "Capture → Sales Order (Excel import)",
    group: "Capture → D365",
    custom: "salesorder-import",
    fields: [],
  },
};

export function groupedScenarios(): Record<string, { id: string; label: string }[]> {
  const groups: Record<string, { id: string; label: string }[]> = {};
  for (const [id, s] of Object.entries(SCENARIOS)) {
    (groups[s.group] ??= []).push({ id, label: s.label });
  }
  return groups;
}
