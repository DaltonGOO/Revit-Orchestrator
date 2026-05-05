<#
.SYNOPSIS
    Install Revit Orchestrator on this machine.

.DESCRIPTION
    This script is meant to be run from inside the unzipped package folder.
    It copies everything to the Revit Addins folder and writes the .addin
    manifest so Revit loads it on startup.

    No Python installation is required - the server is a compiled executable.

.PARAMETER RevitVersion
    Target Revit version (default: 2025)

.EXAMPLE
    .\install.ps1
    .\install.ps1 -RevitVersion 2026
#>
param(
    [string]$RevitVersion = "2025"
)

$ErrorActionPreference = "Stop"

$PackageDir    = $PSScriptRoot
$AddinsRoot    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$DeployDir     = Join-Path $AddinsRoot "RevitOrchestrator"
$AddinManifest = Join-Path $AddinsRoot "RevitOrchestrator.addin"

Write-Host "=== Revit Orchestrator Installer ===" -ForegroundColor Cyan
Write-Host "  Revit version  : $RevitVersion"
Write-Host "  Install target : $DeployDir"
Write-Host ""

# --- Preflight checks ---
if (-not (Test-Path (Join-Path $PackageDir "RevitOrchestrator.dll"))) {
    throw "RevitOrchestrator.dll not found. Run this script from the unzipped package folder."
}
if (-not (Test-Path (Join-Path $PackageDir "python-server\orchestrator.exe"))) {
    throw "python-server\orchestrator.exe not found. The package may be incomplete."
}

# --- Step 1: Copy to Revit Addins ---
Write-Host "[1/2] Installing to Revit Addins folder..." -ForegroundColor Yellow

# Remove any old installation
if (Test-Path $DeployDir) {
    $item = Get-Item $DeployDir -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        cmd /c rmdir $DeployDir 2>&1 | Out-Null
    } else {
        Remove-Item $DeployDir -Recurse -Force
    }
}

# Remove any old loose DLL from older manual installs
$oldDll = Join-Path $AddinsRoot "RevitOrchestrator.dll"
if (Test-Path $oldDll) { Remove-Item $oldDll -Force }

# Ensure the addins directory exists
New-Item -ItemType Directory -Path $AddinsRoot -Force | Out-Null

# Copy the full package (DLLs + python-server exe)
robocopy $PackageDir $DeployDir /E /NFL /NDL /NJH /NJS /NS /NC `
    /XF "install.ps1" | Out-Null

Write-Host "  Installed to: $DeployDir" -ForegroundColor Gray

# --- Step 2: Write .addin manifest ---
Write-Host "[2/2] Writing .addin manifest..." -ForegroundColor Yellow
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit Orchestrator</Name>
    <Assembly>RevitOrchestrator\RevitOrchestrator.dll</Assembly>
    <FullClassName>RevitOrchestrator.App</FullClassName>
    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>
    <VendorId>RevitOrchestrator</VendorId>
    <VendorDescription>Revit Orchestrator - AI-powered Revit automation</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path $AddinManifest -Value $manifest -Encoding UTF8
Write-Host "  Wrote: $AddinManifest" -ForegroundColor Gray

# --- Done ---
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  1. Open Revit $RevitVersion - the Orchestrator panel will appear in the ribbon." -ForegroundColor White
Write-Host "  2. Click the Settings gear in the chat panel." -ForegroundColor White
Write-Host "  3. Pick your provider (Claude, OpenAI, or OpenAI-compatible for Ollama / LM Studio)," -ForegroundColor White
Write-Host "     enter your API key, and click Apply." -ForegroundColor White
Write-Host ""
Write-Host "  (The key is stored DPAPI-encrypted under your Windows user." -ForegroundColor Gray
Write-Host "   ANTHROPIC_API_KEY / OPENAI_API_KEY env vars also work if you prefer.)" -ForegroundColor Gray
