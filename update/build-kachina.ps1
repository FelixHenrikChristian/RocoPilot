[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [int]$CompressionThreads = 8,

    [string]$GitHubInstallerUrl = $env:ROCO_KACHINA_GITHUB_URL,

    [string]$CloudflareInstallerUrl = $env:ROCO_KACHINA_CLOUDFLARE_URL,

    [string]$KachinaBuilderUrl = $env:KACHINA_BUILDER_URL,

    [string]$PublishDir,

    [switch]$SkipPublish,

    [switch]$UpdaterOnly
)

$ErrorActionPreference = "Stop"

$updateDir = $PSScriptRoot
$repoRoot = Split-Path $updateDir -Parent
$project = Join-Path $repoRoot "RocoPilot\RocoPilot.csproj"
$configTemplate = Join-Path $updateDir "kachina.config.json"
$effectiveConfig = Join-Path $updateDir ".kachina.config.effective.json"
$builder = Join-Path $updateDir "kachina-builder.exe"
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $publishDir = Join-Path $updateDir "publish"
}
else {
    $publishDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PublishDir))
}
$metadata = Join-Path $updateDir "metadata.json"
$hashedDir = Join-Path $updateDir "hashed"
$updater = Join-Path $updateDir "RocoPilot.update.exe"
$installer = Join-Path $updateDir "RocoPilot.Install.exe"
$icon = Join-Path $repoRoot "RocoPilot\Assets\RocoPilot-Install.ico"

function ConvertTo-CmdArgument {
    param([string]$Value)

    return '"' + $Value.Replace('"', '""') + '"'
}

function Invoke-KachinaBuilder {
    param(
        [string]$Description,
        [string[]]$Arguments
    )

    $commandLine = (
        @($builder) + $Arguments |
            ForEach-Object { ConvertTo-CmdArgument ([string]$_) }
    ) -join " "

    Write-Host $Description
    Write-Host "> $commandLine"
    & $env:ComSpec /d /s /c $commandLine
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Unblock-Executable {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    try {
        Unblock-File -LiteralPath $Path -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $Path -Stream SmartScreen -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Warning "Could not remove Windows download markers from '$Path': $($_.Exception.Message)"
    }
}

function Get-RocoPilotVersion {
    [xml]$projectXml = Get-Content $project -Raw
    $versionNode = $projectXml.Project.PropertyGroup |
        Where-Object { $_.Version } |
        Select-Object -First 1

    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.Version)) {
        throw "Version was not provided and could not be read from '$project'."
    }

    return [string]$versionNode.Version
}

function Ensure-KachinaBuilder {
    if (Test-Path $builder) {
        Unblock-Executable $builder
        return
    }

    $builderUrl = $KachinaBuilderUrl
    if ([string]::IsNullOrWhiteSpace($builderUrl)) {
        throw "kachina-builder.exe was not found at '$builder'. Download it from https://github.com/YuehaiTeam/kachina-installer/releases/latest and place it in the update directory, or pass -KachinaBuilderUrl to download a specific release asset."
    }

    Write-Host "Downloading kachina-builder.exe from $builderUrl"
    try {
        Invoke-WebRequest -Uri $builderUrl -OutFile $builder
    }
    catch {
        throw "Failed to download kachina-builder.exe. Download it manually from https://github.com/YuehaiTeam/kachina-installer/releases/latest and place it at '$builder'. Details: $($_.Exception.Message)"
    }

    if (-not (Test-Path $builder)) {
        throw "kachina-builder.exe was not created at '$builder'."
    }

    Unblock-Executable $builder
}

function New-EffectiveConfig {
    $githubUrl = $GitHubInstallerUrl
    if ([string]::IsNullOrWhiteSpace($githubUrl)) {
        $githubUrl = "https://github.com/FelixHenrikChristian/RocoPilot/releases/latest/download/RocoPilot.Install.exe"
    }

    $config = Get-Content $configTemplate -Raw | ConvertFrom-Json
    $sources = @(
        [pscustomobject]@{
            id = "github"
            name = "GitHub"
            uri = $githubUrl
        }
    )

    if (-not [string]::IsNullOrWhiteSpace($CloudflareInstallerUrl)) {
        $sources += [pscustomobject]@{
            id = "cloudflare"
            name = "Cloudflare"
            uri = $CloudflareInstallerUrl
        }
    }

    $config.source = $sources
    $json = $config | ConvertTo-Json -Depth 8
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($effectiveConfig, $json, $utf8NoBom)

    return $effectiveConfig
}

if (-not (Test-Path $project)) {
    throw "Project file was not found at '$project'."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-RocoPilotVersion
}

Ensure-KachinaBuilder
$config = New-EffectiveConfig

Remove-Item $updater -Force -ErrorAction SilentlyContinue
Invoke-KachinaBuilder "Building Kachina updater..." @(
    "pack",
    "-c",
    $config,
    "-o",
    $updater,
    "--icon",
    $icon
)
if (-not (Test-Path $updater)) {
    throw "kachina-builder pack for updater completed but '$updater' was not created."
}

if ($UpdaterOnly) {
    Write-Host "Updater created: $updater"
    return
}

if (-not $SkipPublish) {
    Write-Host "Publishing RocoPilot $Version ($Runtime)..."
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:Platform=$Platform `
        -p:WindowsPackageType=None `
        -p:PublishProfile= `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $publishDir)) {
    throw "Publish directory was not found at '$publishDir'. Run without -SkipPublish or create it first."
}

Remove-Item $metadata -Force -ErrorAction SilentlyContinue
Remove-Item $hashedDir -Recurse -Force -ErrorAction SilentlyContinue
Invoke-KachinaBuilder "Generating Kachina metadata and hashed payload..." @(
    "gen",
    "-j",
    $CompressionThreads,
    "-i",
    $publishDir,
    "-m",
    $metadata,
    "-o",
    $hashedDir,
    "-r",
    "RocoPilot",
    "-t",
    $Version,
    "-u",
    $updater,
    "-p",
    "RocoPilot.update.exe"
)
if (-not (Test-Path $metadata)) {
    throw "kachina-builder gen completed but '$metadata' was not created."
}
if (-not (Test-Path $hashedDir)) {
    throw "kachina-builder gen completed but '$hashedDir' was not created."
}

Remove-Item $installer -Force -ErrorAction SilentlyContinue
Invoke-KachinaBuilder "Packing Kachina offline installer..." @(
    "pack",
    "-c",
    $config,
    "-m",
    $metadata,
    "-d",
    $hashedDir,
    "-o",
    $installer,
    "--icon",
    $icon
)
if (-not (Test-Path $installer)) {
    throw "kachina-builder pack for installer completed but '$installer' was not created."
}

Write-Host "Kachina installer created: $installer"
