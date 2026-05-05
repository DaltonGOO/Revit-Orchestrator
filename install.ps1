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

.PARAMETER NoPause
    Skip the "Press Enter to exit" prompt at the end. Use when running from
    an existing PowerShell session or from CI.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -RevitVersion 2026
#>
param(
    [string]$RevitVersion = "2025",
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

function Wait-ForExit {
    if (-not $NoPause) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}

try {
    $PackageDir    = $PSScriptRoot
    $AddinsRoot    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    $DeployDir     = Join-Path $AddinsRoot "RevitOrchestrator"
    $AddinManifest = Join-Path $AddinsRoot "RevitOrchestrator.addin"

    Write-Host "=== Revit Orchestrator Installer ===" -ForegroundColor Cyan
    Write-Host "  Revit version  : $RevitVersion"
    Write-Host "  Install target : $DeployDir"
    Write-Host ""

    if ([string]::IsNullOrEmpty($PackageDir)) {
        throw "Could not determine the script's folder. Run install.ps1 as a file (e.g. '.\install.ps1'), not by pasting its contents."
    }

    # --- Preflight checks ---
    if (-not (Test-Path (Join-Path $PackageDir "RevitOrchestrator.dll"))) {
        throw "RevitOrchestrator.dll not found in '$PackageDir'. Run this script from the unzipped package folder (the one that contains RevitOrchestrator.dll and python-server\)."
    }
    if (-not (Test-Path (Join-Path $PackageDir "python-server\orchestrator.exe"))) {
        throw "python-server\orchestrator.exe not found in '$PackageDir'. The package may be incomplete."
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

    # Copy the full package (DLLs + python-server exe). robocopy returns
    # non-zero exit codes for non-error conditions (e.g. 1 = files copied),
    # so don't trip $ErrorActionPreference on it.
    robocopy $PackageDir $DeployDir /E /NFL /NDL /NJH /NJS /NS /NC `
        /XF "install.ps1" "install.bat" | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE while copying to '$DeployDir'."
    }

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

    Wait-ForExit
}
catch {
    Write-Host ""
    Write-Host "Installation FAILED:" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Wait-ForExit
    exit 1
}
