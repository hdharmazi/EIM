<#
.SYNOPSIS
    Repairs MESSAGEQUEUETABLE.SOURCEID values truncated to 30 characters.

.DESCRIPTION
    Migration 009 widened the column; this restores the values it had already lost. The full id
    survives in PAYLOAD, which is the raw source message, so it is recovered by finding the
    truncated prefix and reading to the closing quote. Reading to the quote rather than assuming
    36 characters means a non-GUID key is repaired correctly too.

    Run deliberately, out of hours. This is not a migration and is not run by the deploy pipeline:
    recovering each value means scanning an NVARCHAR(MAX) payload, and doing all of them in one
    transaction exceeded thirty minutes while holding locks on a table BAT and TEST write to.

    Safe to stop and re-run. Progress is the shrinking count of rows still at 30 characters, so an
    interrupted run simply resumes.

    Runs through sqlcmd. The batching loop belongs server-side, and loading
    Microsoft.Data.SqlClient into PowerShell is not reliable on this machine.

.PARAMETER ConnectionString
    Target database. Prefer -UseEnvironment so it never reaches a command line.

.PARAMETER UseEnvironment
    Read the connection string from EIMDB_MIGRATOR_CONNECTION.

.PARAMETER BatchSize
    Rows per transaction. Smaller means shorter locks and more round trips.

.EXAMPLE
    $env:EIMDB_MIGRATOR_CONNECTION = '<connection string>'
    ./Script/Repair-TruncatedSourceId.ps1 -UseEnvironment -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConnectionString,
    [switch]$UseEnvironment,
    [int]$BatchSize = 2000
)

$ErrorActionPreference = 'Stop'

if ($UseEnvironment) {
    $ConnectionString = [Environment]::GetEnvironmentVariable('EIMDB_MIGRATOR_CONNECTION')
}
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "No connection string. Pass -ConnectionString, or set EIMDB_MIGRATOR_CONNECTION and pass -UseEnvironment."
}
if ($BatchSize -lt 1 -or $BatchSize -gt 20000) {
    throw "BatchSize must be between 1 and 20000."
}
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd not found on PATH."
}

$server   = [regex]::Match($ConnectionString, 'Data Source=tcp:([^,;]+)').Groups[1].Value
$database = [regex]::Match($ConnectionString, 'Initial Catalog=([^;]+)').Groups[1].Value
$user     = [regex]::Match($ConnectionString, 'User ID=([^;]+)').Groups[1].Value
$password = [regex]::Match($ConnectionString, 'Password=([^;]+)').Groups[1].Value

if (-not $server -or -not $database -or -not $user) {
    throw "Could not parse server, database and user from the connection string."
}

function Invoke-Sql {
    param([string]$Sql, [int]$LoginTimeout = 30)

    # Through a file rather than -Q: the repair script contains quotes, and sqlcmd parses those out
    # of a command line argument and then complains about what is left.
    $file = [IO.Path]::GetTempFileName() + '.sql'
    try {
        # -WhatIf:$false because the caller's -WhatIf must not suppress this script's own scratch
        # file. Without it the file is never written and sqlcmd is handed a path that does not exist.
        Set-Content -Path $file -Value $Sql -Encoding utf8 -WhatIf:$false
        # -I sets QUOTED_IDENTIFIER ON, which sqlcmd leaves off by default. MESSAGEQUEUETABLE has a
        # filtered index (IX_MESSAGEQUEUETABLE_DEDUPEKEY) and SQL Server refuses any UPDATE against
        # such a table with the option off.
        $out = sqlcmd -S $server -d $database -U $user -P $password -l $LoginTimeout -b -h -1 -W -I -i $file 2>&1
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $($out -join ' ')" }
        return $out
    }
    finally {
        Remove-Item $file -Force -ErrorAction SilentlyContinue -WhatIf:$false
    }
}

Write-Host "Target: $database on $server"

$width = [int](Invoke-Sql "SET NOCOUNT ON; SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='MESSAGEQUEUETABLE' AND COLUMN_NAME='SOURCEID';" | Select-Object -First 1)
if ($width -lt 150) {
    throw "SOURCEID is still NVARCHAR($width). Apply migration 009 first, or the recovered values are truncated again on the way in."
}

$remaining = [int](Invoke-Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MESSAGEQUEUETABLE WHERE LEN(SOURCEID)=30;" | Select-Object -First 1)
Write-Host "Rows truncated at 30 characters: $remaining"

if ($remaining -eq 0) { Write-Host "Nothing to repair."; return }

if (-not $PSCmdlet.ShouldProcess("$remaining row(s) in $database", "repair SOURCEID")) {
    Write-Host "WhatIf: would repair $remaining row(s) in batches of $BatchSize. Nothing changed."
    return
}

# One transaction per batch, looping server-side. The trigger is disabled inside each batch rather
# than across the run: left off throughout, every unrelated write from BAT or TEST would silently
# skip its MODIFIEDDATETIME stamp. Per batch, other writers wait briefly instead.
$quote = [char]34
$repair = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @BatchSize INT = $BatchSize;
DECLARE @rows INT = 1;
DECLARE @total INT = 0;
DECLARE @msg NVARCHAR(200);

WHILE @rows > 0
BEGIN
    BEGIN TRANSACTION;

    DISABLE TRIGGER dbo.MODIFIEDDATETIME ON dbo.MESSAGEQUEUETABLE;

    WITH candidate AS (
        SELECT TOP (@BatchSize) ID, SOURCEID, CAST(PAYLOAD AS NVARCHAR(MAX)) AS P
        FROM dbo.MESSAGEQUEUETABLE
        WHERE LEN(SOURCEID) = 30
    ),
    located AS (
        SELECT ID, SOURCEID, SUBSTRING(P, CHARINDEX(SOURCEID, P), 200) AS TAIL
        FROM candidate
        WHERE CHARINDEX(SOURCEID, P) > 0
    ),
    repaired AS (
        SELECT ID, SOURCEID, LEFT(TAIL, NULLIF(CHARINDEX('$quote', TAIL), 0) - 1) AS FULLVALUE
        FROM located
    )
    UPDATE m
    SET m.SOURCEID = r.FULLVALUE
    FROM dbo.MESSAGEQUEUETABLE AS m
    INNER JOIN repaired AS r ON r.ID = m.ID
    WHERE r.FULLVALUE IS NOT NULL
      -- Only lengthen, never rewrite: the recovered value must extend the stored one, or the
      -- match found something else in the payload.
      AND r.FULLVALUE LIKE r.SOURCEID + '%'
      AND LEN(r.FULLVALUE) > 30
      AND LEN(r.FULLVALUE) <= 150;

    SET @rows = @@ROWCOUNT;

    ENABLE TRIGGER dbo.MODIFIEDDATETIME ON dbo.MESSAGEQUEUETABLE;

    COMMIT TRANSACTION;

    SET @total = @total + @rows;
    SET @msg = CONCAT('  repaired ', @total);
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SELECT CONCAT('repaired=', @total,
              ' still_truncated=', (SELECT COUNT(*) FROM dbo.MESSAGEQUEUETABLE WHERE LEN(SOURCEID) = 30),
              ' trigger_disabled=', (SELECT CAST(is_disabled AS INT) FROM sys.triggers WHERE name = 'MODIFIEDDATETIME'));
"@

$started = Get-Date
$result = Invoke-Sql -Sql $repair -LoginTimeout 60
$result | ForEach-Object { Write-Host $_ }

$summary = $result | Where-Object { $_ -match 'repaired=' } | Select-Object -Last 1
Write-Host ""
Write-Host "Elapsed: $([int]((Get-Date) - $started).TotalSeconds)s"

if ($summary -notmatch 'trigger_disabled=0') {
    throw "The MODIFIEDDATETIME trigger is still disabled. Re-enable it before anything else writes to the table: ENABLE TRIGGER dbo.MODIFIEDDATETIME ON dbo.MESSAGEQUEUETABLE;"
}
if ($summary -notmatch 'still_truncated=0') {
    Write-Warning "Rows remain at exactly 30 characters. Either their payload does not contain the id, or the value is genuinely 30 characters long. Inspect before assuming failure."
}
