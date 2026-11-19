#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\MDViewer\MDViewer.csproj"
$targetDir = Join-Path $root "src\MDViewer\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"

# WinUI/WASDK *.mui locales to keep (app UI is Russian; en as framework fallback).
$KeepCultures = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
@("ru-RU", "en-us", "en-US", "en-GB") | ForEach-Object { [void]$KeepCultures.Add($_) }

Get-Process MDViewer -ErrorAction SilentlyContinue | Stop-Process -Force

function Invoke-Robocopy([string]$Source, [string]$Dest, [string[]]$ExtraArgs) {
    # Do NOT pipe robocopy to Out-Null — PowerShell can truncate the copy mid-run.
    $allArgs = @($Source, $Dest) + $ExtraArgs + @("/NFL", "/NDL", "/NJH", "/NJS", "/nc", "/ns", "/np")
    & robocopy @allArgs
    $code = $LASTEXITCODE
    # robocopy: 0-7 = success / extra files; >= 8 = failure
    if ($code -ge 8) {
        throw "robocopy failed ($code): $Source -> $Dest"
    }
    $global:LASTEXITCODE = 0
}

function Remove-UnusedCultures([string]$OutDir) {
    $removed = 0
    $kept = New-Object System.Collections.Generic.List[string]

    foreach ($dir in Get-ChildItem -LiteralPath $OutDir -Directory) {
        $mui = @(Get-ChildItem -LiteralPath $dir.FullName -Filter "*.mui" -File -ErrorAction SilentlyContinue)
        if ($mui.Count -eq 0) {
            continue
        }

        if ($script:KeepCultures.Contains($dir.Name)) {
            $kept.Add($dir.Name) | Out-Null
            continue
        }

        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
        $removed++
    }

    Write-Host ("    Cultures kept: [{0}]; removed: {1}" -f (($kept | Sort-Object) -join ", "), $removed)
}

function Remove-Pdbs([string]$OutDir) {
    Get-ChildItem -LiteralPath $OutDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Write-PublishStats([string]$OutDir) {
    $size = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("    Size: {0:N1} MB, files: {1}" -f ($size / 1MB), @(Get-ChildItem -LiteralPath $OutDir -Recurse -File).Count)
}

function Repair-WinUiPublishLayout([string]$BuildDir, [string]$OutDir) {
    # dotnet publish for unpackaged WinUI often drops app PRI/XBF/Assets even though build output has them.
    $assetsSrc = Join-Path $BuildDir "Assets"
    $assetsDst = Join-Path $OutDir "Assets"
    if (Test-Path -LiteralPath $assetsSrc) {
        Invoke-Robocopy $assetsSrc $assetsDst @("/E")
    }

    Get-ChildItem -LiteralPath $BuildDir -Filter "*.xbf" -File -ErrorAction SilentlyContinue |
        Copy-Item -Destination $OutDir -Force

    $pri = Join-Path $BuildDir "MDViewer.pri"
    if (Test-Path -LiteralPath $pri) {
        Copy-Item -LiteralPath $pri -Destination $OutDir -Force
    }
}

# --- Self-contained: publish runtime-bundled build, then repair WinUI layout from that publish's build output ---
$scDir = Join-Path $PSScriptRoot "self-contained"
Write-Host "==> self-contained -> $scDir" -ForegroundColor Cyan
if (Test-Path -LiteralPath $scDir) {
    Remove-Item -LiteralPath $scDir -Recurse -Force
}
dotnet publish $project `
    -c Release `
    -p:Platform=x64 `
    -p:PublishProfile=win-x64-self-contained `
    -p:PublishDir=$scDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed: self-contained" }
Repair-WinUiPublishLayout $targetDir $scDir
Remove-Pdbs $scDir
Remove-UnusedCultures $scDir
Write-PublishStats $scDir

# --- Framework-dependent: use the working Release output as-is (same as bin\...\win-x64) ---
# Avoids incomplete `dotnet publish` file sets that omit PRI/XBF/Assets and break WinUI/WebView.
$fddDir = Join-Path $PSScriptRoot "framework-dependent"
Write-Host "==> framework-dependent (copy Release output) -> $fddDir" -ForegroundColor Cyan
dotnet build $project -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed: Release" }
if (-not (Test-Path -LiteralPath $targetDir)) {
    throw "Release output missing: $targetDir"
}
if (Test-Path -LiteralPath $fddDir) {
    Remove-Item -LiteralPath $fddDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fddDir | Out-Null
# Exclude local WebView2 user-data folders accidentally created under bin during smoke runs.
Invoke-Robocopy $targetDir $fddDir @("/E", "/XD", "MDViewer.exe.WebView2")
Get-ChildItem -LiteralPath $fddDir -Directory -Filter "*.WebView2" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Remove-Pdbs $fddDir
Remove-UnusedCultures $fddDir
Write-PublishStats $fddDir

# Sanity: without Assets/viewer WebView stays blank ("files don't open").
$viewer = Join-Path $fddDir "Assets\viewer\viewer.html"
$pri = Join-Path $fddDir "MDViewer.pri"
if (-not (Test-Path -LiteralPath $viewer) -or -not (Test-Path -LiteralPath $pri)) {
    throw "FDD publish incomplete (missing viewer.html or MDViewer.pri)."
}

Write-Host "Done." -ForegroundColor Green
