<#
.SYNOPSIS
    Build and deploy Revit Orchestrator to the local Revit addins folder.

.DESCRIPTION
    1. Builds the C# addin
    2. Ensures the Python venv exists
    3. Assembles everything into a self-contained folder under Revit Addins
    4. Creates the .addin manifest

    After running this script, just open Revit -- the addin loads and
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

# Find the build output directory -- search for RevitOrchestrator.dll under bin/.
# The csproj's ForceCopyPackageRuntimeDeps target copies NuGet runtime DLLs
# (System.Text.Encoding.CodePages.dll especially) alongside the main DLL, so
# this folder is self-contained and can be copied as-is to the deploy target.
$ProjDir = Split-Path $CSharpProj
$BuildOutput = Get-ChildItem (Join-Path $ProjDir "bin") -Recurse -Filter "RevitOrchestrator.dll" |
    Where-Object { $_.DirectoryName -like "*$Configuration*" } |
    Select-Object -First 1 |
    ForEach-Object { $_.DirectoryName }

if (-not $BuildOutput -or -not (Test-Path $BuildOutput)) {
    throw "Cannot find RevitOrchestrator.dll under bin/. Did the build succeed?"
}

# Sanity-check that our package runtime DLL actually got copied. If it
# didn't, the IronPython encoding fix won't take effect at runtime, so fail
# loudly rather than letting the user discover the silent regression later.
$codePagesDll = Join-Path $BuildOutput "System.Text.Encoding.CodePages.dll"
if (-not (Test-Path $codePagesDll)) {
    throw "System.Text.Encoding.CodePages.dll missing from $BuildOutput. Check the ForceCopyPackageRuntimeDeps target in RevitOrchestrator.csproj."
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

# Hard fail if Revit is running -- it holds RevitOrchestrator.dll open and the
# copy below would silently error out, leaving the user wondering why the
# fix they just made didn't take effect. Better to stop here with a clear
# instruction than to "succeed" with stale bits.
$revitProcs = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revitProcs) {
    Write-Host ""
    Write-Host "ERROR: Revit is running -- close it before deploying." -ForegroundColor Red
    Write-Host "       The DLL would be locked and copy would silently fail." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Running Revit processes:" -ForegroundColor Yellow
    $revitProcs | ForEach-Object {
        Write-Host ("    PID {0}  {1}" -f $_.Id, $_.MainWindowTitle) -ForegroundColor Yellow
    }
    Write-Host ""
    throw "Close Revit and re-run this script."
}

# Remove any old loose DLL/addin in the addins root (from manual installs)
$oldDll = Join-Path $AddinsRoot "RevitOrchestrator.dll"
if (Test-Path $oldDll) {
    Remove-Item $oldDll -Force
    Write-Host "  Removed old loose DLL from addins root" -ForegroundColor Gray
}

# Create deploy directory
New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null

# Copy C# build output (DLL + dependencies). Stop on the first copy error so
# we don't half-update the deployment.
Get-ChildItem $BuildOutput -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $DeployDir -Force -ErrorAction Stop
}
Write-Host "  Copied C# files" -ForegroundColor Gray

# Sanity: confirm the freshly-built DLL actually replaced the deployed one.
$deployedDll = Join-Path $DeployDir "RevitOrchestrator.dll"
$builtDll = Join-Path $BuildOutput "RevitOrchestrator.dll"
$builtTime = (Get-Item $builtDll).LastWriteTime
$deployedTime = (Get-Item $deployedDll).LastWriteTime
if ($deployedTime -lt $builtTime) {
    throw "Deploy DLL ($deployedTime) is older than build DLL ($builtTime). Copy may have silently failed."
}
Write-Host "  Verified deployed DLL is fresh ($deployedTime)" -ForegroundColor Gray

# Copy IronPython standard library so `import io`/`json`/`os` work in tool
# scripts. pyRevit ships IronPython but not its Lib/, so we ship our own.
# Auto-download from NuGet on first deploy so a fresh clone Just Works.
$StdLibSource = Join-Path $RepoRoot "src\revit-addin\python-stdlib"
$StdLibLib    = Join-Path $StdLibSource "Lib"
$StdLibDest   = Join-Path $DeployDir "python-stdlib"

if (-not (Test-Path $StdLibLib)) {
    Write-Host "  Downloading IronPython.StdLib 3.4.2 from NuGet..." -ForegroundColor Gray
    $tmp = Join-Path $env:TEMP "ipy-stdlib-$(New-Guid).nupkg"
    $tmpDir = Join-Path $env:TEMP "ipy-stdlib-$(New-Guid)"
    try {
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/IronPython.StdLib/3.4.2" `
            -OutFile $tmp -UseBasicParsing
        Expand-Archive -Path $tmp -DestinationPath $tmpDir -Force
        $libSrc = Join-Path $tmpDir "contentFiles\any\any\lib"
        if (-not (Test-Path $libSrc)) { throw "stdlib package layout unexpected" }
        New-Item -ItemType Directory -Path $StdLibSource -Force | Out-Null
        Copy-Item $libSrc -Destination $StdLibLib -Recurse -Force
        Write-Host "  Downloaded IronPython stdlib to $StdLibSource" -ForegroundColor Gray
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    }
}

if (Test-Path $StdLibSource) {
    if (Test-Path $StdLibDest) { Remove-Item $StdLibDest -Recurse -Force }
    robocopy $StdLibSource $StdLibDest /E /NFL /NDL /NJH /NJS /NS /NC `
        /XD __pycache__ /XF "*.pyc" | Out-Null
    $stdlibFileCount = (Get-ChildItem $StdLibDest -Recurse -Filter '*.py').Count
    Write-Host "  Copied IronPython stdlib ($stdlibFileCount .py files)" -ForegroundColor Gray
} else {
    Write-Host "  WARNING: $StdLibSource not found - tool scripts using stdlib imports will fail" -ForegroundColor Yellow
}

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
        Write-Host "  Linked Python server (junction -> $McpServer)" -ForegroundColor Gray
    }
} catch { }

if (-not $junctionCreated) {
    Write-Host "  Junction failed, copying Python server..." -ForegroundColor Gray
    robocopy $McpServer $PythonDest /E /NFL /NDL /NJH /NJS /NS /NC `
        /XD __pycache__ .git .mypy_cache .ruff_cache `
        /XF "*.pyc" | Out-Null
    Write-Host "  Copied Python server + venv" -ForegroundColor Gray
}

# --- Step 3.5: Configure pyRevit (extension path + Routes) ---
# Idempotent: pyrevit CLI commands either no-op or update.
$pyrevitCli = $null
foreach ($candidate in @(
    "$env:APPDATA\pyRevit-Master\bin\pyrevit.exe",
    "$env:PROGRAMDATA\pyRevit-Master\bin\pyrevit.exe",
    "$env:LOCALAPPDATA\pyRevit-CLI\bin\pyrevit.exe"))
{
    if (Test-Path $candidate) { $pyrevitCli = $candidate; break }
}
if (-not $pyrevitCli) {
    $cmd = Get-Command pyrevit -ErrorAction SilentlyContinue
    if ($cmd) { $pyrevitCli = $cmd.Source }
}

if ($pyrevitCli) {
    Write-Host "[3.5/4] Configuring pyRevit ($pyrevitCli)..." -ForegroundColor Yellow
    $extPath = Join-Path $RepoRoot "tools"
    & $pyrevitCli extensions paths add "$extPath" 2>&1 | Out-Null
    Write-Host "  Registered extension path: $extPath" -ForegroundColor Gray
    & $pyrevitCli configs routes enable 2>&1 | Out-Null
    Write-Host "  Enabled pyRevit Routes server" -ForegroundColor Gray
} else {
    Write-Host "[3.5/4] pyRevit CLI not found - skipping auto-config." -ForegroundColor Yellow
    Write-Host "  Manual steps:" -ForegroundColor Yellow
    Write-Host "    pyrevit extensions paths add `"$($RepoRoot)\tools`"" -ForegroundColor Yellow
    Write-Host "    pyrevit configs routes enable" -ForegroundColor Yellow
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
