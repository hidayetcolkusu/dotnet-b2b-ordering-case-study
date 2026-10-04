#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Walks the S1 -> S2 -> S2B -> S3 compatibility experiment on a disposable database.

.DESCRIPTION
    Creates a new, uniquely named database on the target SQL Server (the development container by
    default; -ContainerName and -Port point it at another one), drives it through the
    three schema states and runs the V1 and V2 helper executables against it at each step. The
    default development database is never touched, and the database is dropped at the end unless
    -Keep is passed.

    The connection string is passed to the helpers through an environment variable and is never
    printed.
#>
[CmdletBinding()]
param(
    [string]$EnvFile,
    [string]$Configuration = 'Debug',
    [switch]$Keep,
    [string]$ContainerName = 'b2b-ordering-dev-mssql',
    [int]$Port = 14330
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

# Windows PowerShell 5.1 does not expand $PSScriptRoot in a parameter default, so the fallback
# is resolved here instead.
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repoRoot '.env' }
$apiProject = Join-Path $repoRoot 'src/B2B.Ordering.Api/B2B.Ordering.Api.csproj'

if (-not (Test-Path $EnvFile)) {
    throw "No .env file found at $EnvFile. Copy .env.example to .env and set MSSQL_SA_PASSWORD first."
}

$password = $null
foreach ($line in Get-Content $EnvFile) {
    if ($line -match '^\s*MSSQL_SA_PASSWORD\s*=\s*(.+?)\s*$') { $password = $Matches[1] }
}
if ([string]::IsNullOrWhiteSpace($password)) { throw 'MSSQL_SA_PASSWORD is missing from .env.' }

$databaseName = "B2BOrderingMigrationDemo_$([Guid]::NewGuid().ToString('N'))"
$server = "Server=127.0.0.1,$Port;User Id=sa;Password=" + $password + ';TrustServerCertificate=True;Encrypt=True'
$containerName = $ContainerName
$demoConnection = "$server;Database=$databaseName"

# Administrative statements run through the sqlcmd shipped inside the container, so the script
# needs no SQL client assembly on the host and behaves the same on PowerShell 5.1 and 7.
function Invoke-Sql([string]$sql) {
    docker exec -e SQLCMDPASSWORD=$password $containerName /opt/mssql-tools18/bin/sqlcmd `
        -C -S localhost -U sa -d master -b -Q $sql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed for: $sql" }
}

function Invoke-Helper([string]$helper, [string[]]$helperArgs) {
    $exe = Join-Path $repoRoot "samples/$helper/bin/$Configuration/net10.0/$helper"
    if ([Environment]::OSVersion.Platform -eq 'Win32NT') { $exe += '.exe' }
    if (-not (Test-Path $exe)) { throw "Build the solution first: $exe is missing." }

    $env:MIGRATION_DB_CONNECTION = $demoConnection

    # A non-zero exit is an expected outcome after the contract step, and Windows PowerShell
    # turns a native command's stderr into an error record. Start-Process keeps both streams as
    # plain files, so the expected failure stays data instead of becoming a terminating error.
    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $exe -ArgumentList $helperArgs -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
        $text = ((Get-Content $stdoutFile -Raw), (Get-Content $stderrFile -Raw) |
            Where-Object { $_ } | Out-String).Trim()
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $text }
    }
    finally {
        Remove-Item $stdoutFile, $stderrFile -ErrorAction SilentlyContinue
        Remove-Item Env:MIGRATION_DB_CONNECTION -ErrorAction SilentlyContinue
    }
}

Write-Host "Creating disposable database $databaseName"
Invoke-Sql "CREATE DATABASE [$databaseName];"

try {
    $orderS1 = [Guid]::NewGuid()
    $orderS2 = [Guid]::NewGuid()
    $companyA = '11111111-1111-4111-8111-111111111111'
    $buyerA = 'a1111111-1111-4111-8111-111111111111'

    Write-Host "`n== S1: only CustomerReference exists =="
    dotnet ef database update S1_Initial --project $apiProject --connection $demoConnection | Out-Null
    (Invoke-Helper 'Migration.V1' @('write', '--order', $orderS1, '--company', $companyA, '--user', $buyerA, '--reference', 'PO-S1')).Output
    (Invoke-Helper 'Migration.V1' @('read', '--order', $orderS1)).Output

    Write-Host "`n== S2 expand: both columns exist, V1 and V2 run side by side =="
    dotnet ef database update S2_ExpandExternalReference --project $apiProject --connection $demoConnection | Out-Null
    (Invoke-Helper 'Migration.V1' @('write', '--order', $orderS2, '--company', $companyA, '--user', $buyerA, '--reference', 'PO-S2-FROM-V1')).Output
    Write-Host 'V2 reads a V1 row through the fallback:'
    (Invoke-Helper 'Migration.V2' @('read', '--order', $orderS2)).Output
    Write-Host 'V1 reads a V2 row from the old column:'
    $orderFromV2 = [Guid]::NewGuid()
    (Invoke-Helper 'Migration.V2' @('write', '--order', $orderFromV2, '--company', $companyA, '--user', $buyerA, '--reference', 'PO-S2-FROM-V2')).Output
    (Invoke-Helper 'Migration.V1' @('read', '--order', $orderFromV2)).Output

    Write-Host "`n== Late V1 write, then the separate backfill step =="
    # A V1 instance that keeps running after the expand writes only the old column, so V3 would
    # read nothing for this row until the backfill step runs.
    $lateOrder = [Guid]::NewGuid()
    (Invoke-Helper 'Migration.V1' @('write', '--order', $lateOrder, '--company', $companyA, '--user', $buyerA, '--reference', 'PO-LATE-FROM-V1')).Output
    Write-Host 'V2 still finds it only through the fallback, so V3 would see null:'
    (Invoke-Helper 'Migration.V2' @('read', '--order', $lateOrder)).Output

    Write-Host 'Applying the backfill step (V1 and V2 writers are stopped at this point):'
    dotnet ef database update S2B_BackfillExternalReference --project $apiProject --connection $demoConnection | Out-Null
    Write-Host 'Now the value is in the new column, which is what V3 reads:'
    (Invoke-Helper 'Migration.V2' @('read', '--order', $lateOrder)).Output

    Write-Host "`n== S3 contract: backfill repeated as a guard, column dropped =="
    dotnet ef database update S3_ContractDropCustomerReference --project $apiProject --connection $demoConnection | Out-Null
    Write-Host 'V1 is now incompatible, which is the expected controlled failure:'
    $failed = Invoke-Helper 'Migration.V1' @('read', '--order', $orderS2)
    Write-Host "  exit code $($failed.ExitCode): $($failed.Output)"

    Write-Host "`nDemo complete. The default development database was not modified."
}
finally {
    if (-not $Keep) {
        Write-Host "Dropping $databaseName"
        Invoke-Sql "ALTER DATABASE [$databaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$databaseName];"
    }
    else {
        Write-Host "Keeping $databaseName as requested."
    }
}
