<#
.SYNOPSIS
    Publishes the agent, dashboard and service as one self-contained folder and builds the MSI.
.DESCRIPTION
    Output:
      artifacts\publish\win-<arch>\          ready-to-run application folder (no .NET install needed)
      artifacts\WorkToUnlock-<ver>-win-<arch>.zip
      artifacts\installer\WorkToUnlock-<ver>-<arch>.msi
.EXAMPLE
    ./build/Publish.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [ValidateSet('x64', 'arm64')] [string]$Architecture = 'x64',
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$rid = "win-$Architecture"
$publishDir = Join-Path $root "artifacts\publish\$rid"
$common = @('-c', $Configuration, '-r', $rid, '--self-contained', 'true', "-p:Version=$Version", '-p:DebugType=none', '-nologo')

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
}

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force $publishDir | Out-Null

Invoke-Step 'Publish agent' { dotnet publish (Join-Path $root 'src\WorkToUnlock.Agent\WorkToUnlock.Agent.csproj') @common -o $publishDir }
Invoke-Step 'Publish service' { dotnet publish (Join-Path $root 'src\WorkToUnlock.Service\WorkToUnlock.Service.csproj') @common -o $publishDir }
Invoke-Step 'Publish dashboard' { dotnet publish (Join-Path $root 'src\WorkToUnlock.App\WorkToUnlock.App.csproj') @common "-p:Platform=$Architecture" -o $publishDir }

foreach ($exe in 'WorkToUnlock.Agent.exe', 'WorkToUnlock.App.exe', 'WorkToUnlock.Service.exe') {
    if (-not (Test-Path (Join-Path $publishDir $exe))) { throw "$exe is missing from the publish output." }
}

$zip = Join-Path $root "artifacts\WorkToUnlock-$Version-$rid.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
Write-Host "Portable build: $zip"

if (-not $SkipInstaller) {
    Invoke-Step 'Build MSI' {
        dotnet build (Join-Path $root 'installer\WorkToUnlock.Installer\WorkToUnlock.Installer.wixproj') -c $Configuration "-p:Platform=$Architecture" "-p:ProductVersion=$Version" "-p:PublishDir=$publishDir" -nologo
    }
    Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter *.msi | ForEach-Object { Write-Host "Installer: $($_.FullName)" }
}
