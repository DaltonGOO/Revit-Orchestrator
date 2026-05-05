<#
.SYNOPSIS
    Package Revit Orchestrator into a distributable zip for sharing.

.DESCRIPTION
    1. Builds the C# addin (Release)
    2. Compiles the Python server into a standalone exe via PyInstaller
    3. Bundles everything into a zip in dist/

    No Python source code is shipped - only compiled binaries.
    This does NOT touch your local Revit Addins folder or dev environment.

.PARAMETER RevitVersion
    Target Revit version (default: 2025)

.PARAMETER Configuration
    Build configuration (default: Release)

.EXAMPLE
    .\package.ps1
    .\package.ps1 -RevitVersion 2026
#>
param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Release"
)

# Use Continue so native commands writing to stderr don't terminate.
# We check $LASTEXITCODE manually after each critical command.
$ErrorActionPreference = "Continue"

function Assert-ExitCode($msg) {
    if ($LASTEXITCODE -ne 0) { Write-Error $msg; exit 1 }
}

$RepoRoot     = $PSScriptRoot
$CSharpProj   = Join-Path $RepoRoot "src\revit-addin\src\RevitOrchestrator\RevitOrchestrator.csproj"
$McpServer    = Join-Path $RepoRoot "src\mcp-server"
$VenvPython   = Join-Path $McpServer ".venv\Scripts\python.exe"
$VenvPip      = Join-Path $McpServer ".venv\Scripts\pip.exe"
$ContractsDir = Join-Path $RepoRoot "contracts"
$DistDir      = Join-Path $RepoRoot "dist"
$StageDir     = Join-Path $DistDir "RevitOrchestrator"

Write-Host "=== Revit Orchestrator Packager ===" -ForegroundColor Cyan
Write-Host "  Revit version : $RevitVersion"
Write-Host "  Configuration : $Configuration"
Write-Host ""

# --- Step 1: Build C# ---
Write-Host "[1/5] Building C# addin ($Configuration)..." -ForegroundColor Yellow
dotnet build $CSharpProj -c $Configuration -p:RevitVersion=$RevitVersion 2>&1 | Out-Null
Assert-ExitCode "C# build failed"

$ProjDir = Split-Path $CSharpProj
$BuildOutput = Get-ChildItem (Join-Path $ProjDir "bin") -Recurse -Filter "RevitOrchestrator.dll" |
    Where-Object { $_.DirectoryName -like "*$Configuration*" } |
    Select-Object -First 1 |
    ForEach-Object { $_.DirectoryName }

if (-not $BuildOutput -or -not (Test-Path $BuildOutput)) {
    Write-Error "Cannot find RevitOrchestrator.dll under bin/. Did the build succeed?"; exit 1
}
Write-Host "  Build output: $BuildOutput" -ForegroundColor Gray

# --- Step 2: Ensure Python venv + PyInstaller ---
Write-Host "[2/5] Checking Python environment..." -ForegroundColor Yellow
if (-not (Test-Path $VenvPython)) {
    Write-Host "  Creating venv..." -ForegroundColor Gray
    Push-Location $McpServer
    python -m venv .venv
    & $VenvPython -m pip install -e . 2>&1 | Out-Null
    Pop-Location
}

# Ensure PyInstaller is installed
& $VenvPython -c "import PyInstaller" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Installing PyInstaller..." -ForegroundColor Gray
    & $VenvPip install pyinstaller 2>&1 | Out-Null
    Assert-ExitCode "Failed to install PyInstaller"
}
Write-Host "  Python + PyInstaller OK" -ForegroundColor Gray

# --- Step 3: Build Python exe with PyInstaller ---
Write-Host "[3/5] Building Python server exe (PyInstaller)..." -ForegroundColor Yellow
Write-Host "  This may take a minute..." -ForegroundColor Gray
$PyInstallerDist  = Join-Path $McpServer "dist"
$PyInstallerBuild = Join-Path $McpServer "build"

# Clean previous PyInstaller output
if (Test-Path $PyInstallerDist) { Remove-Item $PyInstallerDist -Recurse -Force }
if (Test-Path $PyInstallerBuild) { Remove-Item $PyInstallerBuild -Recurse -Force }

Push-Location $McpServer
& $VenvPython -m PyInstaller `
    --name orchestrator `
    --onedir `
    --console `
    --noconfirm `
    --clean `
    --add-data "orchestrator\tools;orchestrator\tools" `
    --add-data "$ContractsDir;contracts" `
    --hidden-import orchestrator `
    --hidden-import orchestrator.server `
    --hidden-import orchestrator.config `
    --hidden-import orchestrator.chat_session `
    --hidden-import orchestrator.execution_logger `
    --hidden-import orchestrator.logging_config `
    --hidden-import orchestrator.pipe `
    --hidden-import orchestrator.pipe.connection `
    --hidden-import orchestrator.pipe.protocol `
    --hidden-import orchestrator.registry `
    --hidden-import orchestrator.registry.registry `
    --hidden-import orchestrator.registry.schema_validator `
    --hidden-import orchestrator.dispatcher `
    --hidden-import orchestrator.dispatcher.dispatcher `
    --hidden-import orchestrator.dispatcher.result `
    --hidden-import orchestrator.adapters `
    --hidden-import orchestrator.adapters.workflow `
    --hidden-import orchestrator.adapters.mcp_external `
    --hidden-import orchestrator.llm `
    --hidden-import orchestrator.llm.openai_provider `
    --hidden-import orchestrator.workflow `
    --hidden-import orchestrator.workflow.engine `
    --hidden-import orchestrator.store `
    --hidden-import orchestrator.store.sqlite_store `
    --hidden-import orchestrator.connections `
    --hidden-import orchestrator.reconciliation `
    --hidden-import orchestrator.handlers `
    --hidden-import orchestrator.telemetry `
    --hidden-import orchestrator.safety `
    --hidden-import win32pipe `
    --hidden-import win32file `
    --hidden-import win32event `
    --hidden-import pywintypes `
    entry_point.py 2>&1 | Out-Null
Pop-Location

Assert-ExitCode "PyInstaller build failed"

$ExePath = Join-Path $PyInstallerDist "orchestrator\orchestrator.exe"
if (-not (Test-Path $ExePath)) {
    Write-Error "orchestrator.exe not found at $ExePath"; exit 1
}
Write-Host "  Built: $ExePath" -ForegroundColor Gray

# --- Step 4: Assemble staging directory ---
Write-Host "[4/5] Assembling package..." -ForegroundColor Yellow
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

# Copy C# build output (DLLs + dependencies)
Get-ChildItem $BuildOutput -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $StageDir -Force
}
Write-Host "  Copied C# files" -ForegroundColor Gray

# Copy PyInstaller output as python-server/
$PythonDest = Join-Path $StageDir "python-server"
Copy-Item (Join-Path $PyInstallerDist "orchestrator") -Destination $PythonDest -Recurse -Force
Write-Host "  Copied compiled Python server (no source code)" -ForegroundColor Gray

# Copy sample tool sources (the .cs / .py / .dyn files referenced by tool
# def `script_path` defaults). The installed loader resolves ${REPO_ROOT}
# to the install dir, so these need to be a sibling of python-server\.
$ToolsSrc  = Join-Path $RepoRoot "tools"
$ToolsDest = Join-Path $StageDir "tools"
if (Test-Path $ToolsSrc) {
    robocopy $ToolsSrc $ToolsDest /E /NFL /NDL /NJH /NJS /NS /NC | Out-Null
    Write-Host "  Copied sample tool sources" -ForegroundColor Gray
}

# Copy the install script
Copy-Item (Join-Path $RepoRoot "install.ps1") -Destination $StageDir -Force
Write-Host "  Included install.ps1" -ForegroundColor Gray

# --- Step 5: Create zip ---
Write-Host "[5/5] Creating zip..." -ForegroundColor Yellow
$ZipName = "RevitOrchestrator-v$((Get-Date).ToString('yyyyMMdd'))-Revit$RevitVersion.zip"
$ZipPath = Join-Path $DistDir $ZipName

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal
Write-Host "  Created: $ZipPath" -ForegroundColor Gray

# Clean up staging folder + PyInstaller temp
Remove-Item $StageDir -Recurse -Force
Remove-Item $PyInstallerBuild -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $PyInstallerDist -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $McpServer "orchestrator.spec") -Force -ErrorAction SilentlyContinue

$SizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "Package complete!" -ForegroundColor Green
Write-Host "  File: $ZipPath"
Write-Host "  Size: ${SizeMB} MB"
Write-Host "  Contents: C# DLLs + compiled Python exe (no source code)"
Write-Host ""
Write-Host "Send this zip to your tester. They should:" -ForegroundColor Cyan
Write-Host "  1. Unzip anywhere"
Write-Host "  2. Right-click install.ps1 -> Run with PowerShell"
Write-Host "  3. Set their ANTHROPIC_API_KEY or OPENAI_API_KEY env var"
Write-Host "  4. Open Revit $RevitVersion"
