[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $PublishProfileEnvironmentVariable
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$package = Resolve-Path $PackagePath -ErrorAction Stop
$profileXml = [Environment]::GetEnvironmentVariable($PublishProfileEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($profileXml)) {
    throw "Secret environment variable '$PublishProfileEnvironmentVariable' is not set."
}

try {
    [xml] $profileDocument = $profileXml
} catch {
    throw "'$PublishProfileEnvironmentVariable' does not contain a valid Azure publish-profile XML document."
}

$publishProfile = @($profileDocument.publishData.publishProfile) |
    Where-Object { $_.publishMethod -in @("MSDeploy", "ZipDeploy") } |
    Select-Object -First 1
if ($null -eq $publishProfile) {
    throw "The publish profile does not contain an MSDeploy or ZipDeploy entry."
}

$publishUrl = [string] $publishProfile.publishUrl
$userName = [string] $publishProfile.userName
$password = [string] $publishProfile.userPWD
if ([string]::IsNullOrWhiteSpace($publishUrl) -or
    [string]::IsNullOrWhiteSpace($userName) -or
    [string]::IsNullOrWhiteSpace($password)) {
    throw "The publish profile is missing publishUrl, userName, or userPWD."
}

$hostName = ($publishUrl -replace '^https?://', '').Split('/')[0].Split(':')[0]
$deployUri = "https://$hostName/api/zipdeploy?isAsync=true"
$basicToken = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${userName}:${password}"))
$headers = @{ Authorization = "Basic $basicToken" }

Write-Host "Uploading package '$($package.Path)' to $hostName"
try {
    $response = Invoke-WebRequest `
        -Uri $deployUri `
        -Method POST `
        -Headers $headers `
        -ContentType "application/zip" `
        -InFile $package.Path `
        -UseBasicParsing
} catch {
    if ($_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::Unauthorized) {
        throw "Kudu rejected the publish profile with HTTP 401. Confirm SCM Basic Auth Publishing Credentials are enabled for the target App Service/Function App, download a fresh publish profile, and update the secret pipeline variable."
    }

    throw
}

$statusUri = [string] $response.Headers.Location
if ([string]::IsNullOrWhiteSpace($statusUri)) {
    Write-Host "ZIP deployment request completed with HTTP $($response.StatusCode)."
    exit 0
}

$deadline = (Get-Date).AddMinutes(15)
do {
    if ((Get-Date) -ge $deadline) {
        throw "Timed out waiting for ZIP deployment to complete."
    }

    Start-Sleep -Seconds 5
    $status = Invoke-RestMethod -Uri $statusUri -Method GET -Headers $headers
    Write-Host "Deployment status: $($status.status_text)"
} while ([int] $status.status -in @(0, 1, 2))

if ([int] $status.status -ne 4) {
    throw "ZIP deployment failed: $($status.message) $($status.log_url)"
}

Write-Host "ZIP deployment completed successfully."
