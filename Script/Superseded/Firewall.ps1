Connect-AzAccount

Connect-AzAccount -Tenant "446b6761-0bd4-4dad-821a-cbdbe4b7b4de"   -Subscription "84abae0f-07f1-4df8-aacd-197d2e7f1cac"

$myIp = Invoke-RestMethod -Uri "https://api.ipify.org"

New-AzSqlServerFirewallRule `
  -ResourceGroupName "d365fsc-integration-nonprod" `
  -ServerName "azql-wtc-sandbox" `
  -FirewallRuleName "AllowMyIP" `
  -StartIpAddress $myIp `
  -EndIpAddress $myIp