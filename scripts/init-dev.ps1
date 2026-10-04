#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prepares the local development database for the API.

.DESCRIPTION
    Waits for the compose database to report healthy, stores the development connection string
    in dotnet user-secrets (never in a file inside the repository) and runs the API's
    --initialize-db mode to apply migrations and seed synthetic data.

    The SA password is read from the local .env file and is never written to stdout.

    The container name, host port and database name are parameters with the ordinary development
    values as defaults, so a disposable instance can be initialised beside the development one
    without touching it.
#>
[CmdletBinding()]
param(
    [string]$EnvFile,
    [int]$TimeoutSeconds = 180,
    [string]$ContainerName = 'b2b-ordering-dev-mssql',
    [int]$Port = 14330,
    [string]$Database = 'B2BOrderingDev'
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
    if ($line -match '^\s*MSSQL_SA_PASSWORD\s*=\s*(.+?)\s*$') {
        $password = $Matches[1]
    }
}

if ([string]::IsNullOrWhiteSpace($password) -or $password -eq 'replace-me-with-a-locally-generated-password') {
    throw 'MSSQL_SA_PASSWORD is missing or still the placeholder value in .env.'
}

Write-Host 'Waiting for the development database container to become healthy...'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$containerName = $ContainerName
while ($true) {
    $status = (docker inspect --format '{{.State.Health.Status}}' $containerName 2>$null)
    if ($status -eq 'healthy') { break }
    if ((Get-Date) -gt $deadline) {
        throw "Container '$containerName' did not become healthy within $TimeoutSeconds seconds. Run 'docker compose up -d' first."
    }
    Start-Sleep -Seconds 3
}
Write-Host 'Database container is healthy.'

# TrustServerCertificate is acceptable only because this server is a loopback dev container.
$connectionString = "Server=127.0.0.1,$Port;Database=$Database;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=True"

Write-Host 'Storing the development connection string in user-secrets...'
dotnet user-secrets --project $apiProject set 'ConnectionStrings:Default' $connectionString | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to write the connection string to user-secrets.' }

Write-Host 'Applying migrations and seeding synthetic data...'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project $apiProject -- --initialize-db
if ($LASTEXITCODE -ne 0) { throw 'Database initialisation failed.' }

Write-Host 'Development database is ready. No secret was printed by this script.'
