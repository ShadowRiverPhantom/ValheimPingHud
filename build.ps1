<#
.SYNOPSIS
    Builds ValheimPingHud.dll with the Roslyn compiler that ships with Visual Studio.

.DESCRIPTION
    Valheim mods are compiled against the game's own Mono assemblies, so a .NET SDK
    is not required - the csc.exe from Visual Studio 2019/2022 is enough.
    If an SDK is present, MSBuild is used as a fallback.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Deploy
#>
[CmdletBinding()]
param(
    [string]$ValheimDir,
    [string]$BepInExCoreDir,
    [string]$OutputDir,
    [string]$Version,
    [switch]$Deploy,
    [switch]$NoPackage,
    [string]$ProfilePluginsDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }
$assemblyName = 'ValheimPingHud'

if (-not $Version) {
    $manifestPath = Join-Path $root 'manifest.json'
    if (Test-Path $manifestPath) {
        $Version = (Get-Content -Path $manifestPath -Raw | ConvertFrom-Json).version_number
    }
    if (-not $Version) { $Version = '1.0.0' }
}

function Find-ValheimDir {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path (Join-Path $Explicit 'valheim_Data\Managed\assembly_valheim.dll')) { return $Explicit }
        throw "ValheimDir '$Explicit' does not look like a Valheim install."
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:VALHEIM_DIR) { $candidates.Add($env:VALHEIM_DIR) }

    $steamRoots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath) { $steamRoots.Add($item.SteamPath) }
            if ($item.InstallPath) { $steamRoots.Add($item.InstallPath) }
        } catch { }
    }
    $steamRoots.Add('C:\Program Files (x86)\Steam')

    foreach ($steamRoot in $steamRoots) {
        if (-not $steamRoot) { continue }
        $steamRoot = $steamRoot -replace '/', '\'
        $candidates.Add((Join-Path $steamRoot 'steamapps\common\Valheim'))

        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $text = Get-Content -Path $vdf -Raw
            foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $library = $match.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $library 'steamapps\common\Valheim'))
            }
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'valheim_Data\Managed\assembly_valheim.dll'))) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw 'Valheim not found. Pass -ValheimDir "D:\...\Valheim" or set $env:VALHEIM_DIR.'
}

function Find-BepInExCore {
    param([string]$Valheim, [string]$Explicit)

    if ($Explicit) {
        if (Test-Path (Join-Path $Explicit 'BepInEx.dll')) { return (Resolve-Path $Explicit).Path }
        throw "BepInExCoreDir '$Explicit' does not contain BepInEx.dll."
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:BEPINEX_CORE_DIR) { $candidates.Add($env:BEPINEX_CORE_DIR) }
    $candidates.Add((Join-Path $Valheim 'BepInEx\core'))

    $profilesRoot = Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles'
    if (Test-Path $profilesRoot) {
        $profiles = Get-ChildItem -Path $profilesRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object -Property @{ Expression = { if ($_.Name -eq 'Default') { 0 } else { 1 } } }, Name
        foreach ($profile in $profiles) {
            $candidates.Add((Join-Path $profile.FullName 'BepInEx\core'))
        }
    }

    $cacheRoot = Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\cache'
    if (Test-Path $cacheRoot) {
        $pack = Get-ChildItem -Path $cacheRoot -Recurse -Filter 'BepInEx.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($pack) { $candidates.Add($pack.DirectoryName) }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'BepInEx.dll'))) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw 'BepInEx core not found. Install BepInExPack_Valheim (r2modman) or pass -BepInExCoreDir.'
}

function Find-Csc {
    $command = Get-Command 'csc.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $patterns = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe')
    )

    foreach ($pattern in $patterns) {
        $found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

function Test-DotnetSdk {
    $dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $false }
    $sdks = & $dotnet.Source --list-sdks 2>$null
    return [bool]($sdks -and $sdks.Count -gt 0)
}

# ---------------------------------------------------------------------------

$valheim = Find-ValheimDir -Explicit $ValheimDir
$bepInExCore = Find-BepInExCore -Valheim $valheim -Explicit $BepInExCoreDir
$managed = Join-Path $valheim 'valheim_Data\Managed'

Write-Host "Valheim      : $valheim"
Write-Host "BepInEx core : $bepInExCore"

$referenceFiles = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'netstandard.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'assembly_valheim.dll',
    'assembly_guiutils.dll'
)

$references = New-Object System.Collections.Generic.List[string]
foreach ($file in $referenceFiles) {
    $path = Join-Path $managed $file
    if (Test-Path $path) {
        $references.Add((Resolve-Path $path).Path)
    } else {
        Write-Warning "Reference not found, skipped: $file"
    }
}

foreach ($file in @('BepInEx.dll', '0Harmony.dll')) {
    $path = Join-Path $bepInExCore $file
    if (Test-Path $path) {
        $references.Add((Resolve-Path $path).Path)
    } else {
        Write-Warning "Reference not found, skipped: $file"
    }
}

$sources = Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' -Recurse |
    Sort-Object -Property FullName | ForEach-Object { $_.FullName }
if (-not $sources) { throw 'No sources found under src\.' }

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
$outputDir = (Resolve-Path $OutputDir).Path
$outputDll = Join-Path $outputDir "$assemblyName.dll"

$csc = Find-Csc
if ($csc) {
    Write-Host "Compiler     : $csc"
    $compilerArgs = @(
        '-noconfig',
        '-nostdlib+',
        '-nologo',
        '-target:library',
        '-langversion:7.3',
        '-optimize+',
        '-debug:portable',
        '-platform:anycpu',
        ('-out:' + $outputDll)
    )
    foreach ($reference in $references) { $compilerArgs += ('-reference:' + $reference) }
    foreach ($source in $sources) { $compilerArgs += $source }

    & $csc @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
} elseif (Test-DotnetSdk) {
    Write-Host 'Compiler     : dotnet build (MSBuild fallback)'
    $project = Join-Path $root 'ValheimPingHud.csproj'
    & dotnet build $project -c Release -p:ValheimDir="$valheim" -p:BepInExCoreDir="$bepInExCore" -o $outputDir
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
} else {
    throw 'No C# compiler found. Install Visual Studio 2022 (with "Desktop development with C#") or the .NET SDK.'
}

Write-Host ''
Write-Host "Built: $outputDll" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Thunderstore / r2modman package (import local mod -> pick the zip)
# ---------------------------------------------------------------------------

$packageRoot = Join-Path $outputDir 'package'
if (-not $NoPackage) {
    if (Test-Path $packageRoot) { Remove-Item -Path $packageRoot -Recurse -Force }
    $packagePlugins = Join-Path $packageRoot 'BepInEx\plugins'
    New-Item -ItemType Directory -Path $packagePlugins -Force | Out-Null

    Copy-Item -Path $outputDll -Destination (Join-Path $packagePlugins "$assemblyName.dll") -Force
    foreach ($extra in @('manifest.json', 'icon.png', 'README.md')) {
        $path = Join-Path $root $extra
        if (-not (Test-Path $path)) { continue }
        if ($extra -eq 'manifest.json') {
            # The package manifest must advertise the built version.
            $manifest = Get-Content -Path $path -Raw | ConvertFrom-Json
            $manifest.version_number = $Version
            $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $packageRoot $extra) -Encoding UTF8
        } else {
            Copy-Item -Path $path -Destination (Join-Path $packageRoot $extra) -Force
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $outputDir "PingHud-$Version.zip"
    if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath)
    Write-Host "Package: $zipPath" -ForegroundColor Green
}

if ($Deploy) {
    if (-not $ProfilePluginsDir) {
        $defaultProfile = Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles\Default\BepInEx\plugins'
        if (Test-Path $defaultProfile) {
            $ProfilePluginsDir = $defaultProfile
        }
    }

    if (-not $ProfilePluginsDir) {
        Write-Warning 'Deploy skipped: no r2modman profile found, pass -ProfilePluginsDir.'
    } else {
        $target = Join-Path $ProfilePluginsDir 'kagegawa-PingHud'
        if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }
        Copy-Item -Path $outputDll -Destination $target -Force
        $pdb = [System.IO.Path]::ChangeExtension($outputDll, '.pdb')
        if (Test-Path $pdb) { Copy-Item -Path $pdb -Destination $target -Force }
        Write-Host "Deployed to: $target" -ForegroundColor Green
    }
}
