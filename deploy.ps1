<#
.SYNOPSIS
    Build and deploy Revit Orchestrator to the local Revit addins folder.

.DESCRIPTION
    1. Builds the C# addin
    2. Ensures the Python venv exists
    3. Assembles everything into a self-contained folder under Revit Addins
    4. Creates the .addin manifest

    After running this script, just open Revit — the addin loads and
    auto-starts the Python server.

.PARAMETER RevitVersion
    Target Revit version (default: 2025)

.PARAMETER Configuration
    Build configuration (default: Debug)

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -RevitVersion 2026 -Configuration Release
#>
param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$RepoRoot    = $PSScriptRoot
$CSharpProj  = Join-Path $RepoRoot "src\revit-addin\src\RevitOrchestrator\RevitOrchestrator.csproj"
$McpServer   = Join-Path $RepoRoot "src\mcp-server"
$VenvPython  = Join-Path $McpServer ".venv\Scripts\python.exe"
$AddinsRoot  = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$DeployDir   = Join-Path $AddinsRoot "RevitOrchestrator"
$AddinManifest = Join-Path $AddinsRoot "RevitOrchestrator.addin"

Write-Host "=== Revit Orchestrator Deploy ===" -ForegroundColor Cyan
Write-Host "  Revit version : $RevitVersion"
Write-Host "  Configuration : $Configuration"
Write-Host "  Deploy target : $DeployDir"
Write-Host ""

# --- Step 1: Build C# ---
Write-Host "[1/4] Building C# addin..." -ForegroundColor Yellow
dotnet build $CSharpProj -c $Configuration -p:RevitVersion=$RevitVersion
if ($LASTEXITCODE -ne 0) { throw "C# build failed" }

# Find the build output directory — search for RevitOrchestrator.dll under bin/
$ProjDir = Split-Path $CSharpProj
$BuildOutput = Get-ChildItem (Join-Path $ProjDir "bin") -Recurse -Filter "RevitOrchestrator.dll" |
    Where-Object { $_.DirectoryName -like "*$Configuration*" } |
    Select-Object -First 1 |
    ForEach-Object { $_.DirectoryName }

if (-not $BuildOutput -or -not (Test-Path $BuildOutput)) {
    throw "Cannot find RevitOrchestrator.dll under bin/. Did the build succeed?"
}
Write-Host "  Build output: $BuildOutput" -ForegroundColor Gray

# --- Step 2: Ensure Python venv ---
Write-Host "[2/4] Checking Python venv..." -ForegroundColor Yellow
if (-not (Test-Path $VenvPython)) {
    Write-Host "  Creating venv..." -ForegroundColor Gray
    Push-Location $McpServer
    python -m venv .venv
    & $VenvPython -m pip install -e . 2>&1 | Out-Null
    Pop-Location
}
Write-Host "  venv OK: $VenvPython" -ForegroundColor Gray

# --- Step 3: Assemble deployment folder ---
Write-Host "[3/4] Assembling deployment folder..." -ForegroundColor Yellow

# Remove any old loose DLL/addin in the addins root (from manual installs)
$oldDll = Join-Path $AddinsRoot "RevitOrchestrator.dll"
if (Test-Path $oldDll) {
    Remove-Item $oldDll -Force
    Write-Host "  Removed old loose DLL from addins root" -ForegroundColor Gray
}

# Create deploy directory
New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null

# Copy C# build output (DLL + dependencies)
Get-ChildItem $BuildOutput -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $DeployDir -Force
}
Write-Host "  Copied C# files" -ForegroundColor Gray

# Link or copy the Python server
$PythonDest = Join-Path $DeployDir "python-server"

# For dev, use a directory junction (instant, zero disk space).
# Falls back to full copy if junction creation fails (e.g. different drive).
if (Test-Path $PythonDest) {
    # Remove existing (junction or real folder)
    $item = Get-Item $PythonDest -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        cmd /c rmdir $PythonDest 2>&1 | Out-Null
    } else {
        Remove-Item $PythonDest -Recurse -Force
    }
}

$junctionCreated = $false
try {
    cmd /c mklink /J $PythonDest $McpServer 2>&1 | Out-Null
    if (Test-Path (Join-Path $PythonDest ".venv\Scripts\python.exe")) {
        $junctionCreated = $true
        Write-Host "  Linked Python server (junction → $McpServer)" -ForegroundColor Gray
    }
} catch { }

if (-not $junctionCreated) {
    Write-Host "  Junction failed, copying Python server..." -ForegroundColor Gray
    robocopy $McpServer $PythonDest /E /NFL /NDL /NJH /NJS /NS /NC `
        /XD __pycache__ .git .mypy_cache .ruff_cache `
        /XF "*.pyc" | Out-Null
    Write-Host "  Copied Python server + venv" -ForegroundColor Gray
}

# --- Step 4: Write .addin manifest ---
Write-Host "[4/4] Writing .addin manifest..." -ForegroundColor Yellow
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
Write-Host "  Wrote $AddinManifest" -ForegroundColor Gray

# --- Done ---
Write-Host ""
Write-Host "Deploy complete!" -ForegroundColor Green
Write-Host "  Addin:  $AddinManifest"
Write-Host "  DLL:    $DeployDir\RevitOrchestrator.dll"
Write-Host "  Python: $PythonDest\.venv\Scripts\python.exe"
Write-Host ""
Write-Host "Open Revit $RevitVersion and the orchestrator will start automatically." -ForegroundColor Cyan
Write-Host ""
Write-Host "NOTE: Make sure ANTHROPIC_API_KEY or OPENAI_API_KEY is set as a" -ForegroundColor DarkYellow
Write-Host "system environment variable so the Python server can reach the LLM." -ForegroundColor DarkYellow
