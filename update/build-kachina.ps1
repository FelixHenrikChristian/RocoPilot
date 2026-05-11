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
$publishDir = Join-Path $updateDir "publish"
$metadata = Join-Path $updateDir "metadata.json"
$hashedDir = Join-Path $updateDir "hashed"
$updater = Join-Path $updateDir "RocoPilot.update.exe"
$installer = Join-Path $updateDir "RocoPilot.Install.exe"
$icon = Join-Path $repoRoot "RocoPilot\Assets\RocoPilot-Install.ico"

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
    $config | ConvertTo-Json -Depth 8 | Set-Content -Path $effectiveConfig -Encoding UTF8

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

Write-Host "Building Kachina updater..."
Remove-Item $updater -Force -ErrorAction SilentlyContinue
& $builder pack -c $config -o $updater --icon $icon
if ($LASTEXITCODE -ne 0) {
    throw "kachina-builder pack for updater failed with exit code $LASTEXITCODE."
}
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

Write-Host "Generating Kachina metadata and hashed payload..."
Remove-Item $metadata -Force -ErrorAction SilentlyContinue
Remove-Item $hashedDir -Recurse -Force -ErrorAction SilentlyContinue
& $builder gen `
    -j $CompressionThreads `
    -i $publishDir `
    -m $metadata `
    -o $hashedDir `
    -r RocoPilot `
    -t $Version `
    -u $updater `
    -p "RocoPilot.update.exe"
if ($LASTEXITCODE -ne 0) {
    throw "kachina-builder gen failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $metadata)) {
    throw "kachina-builder gen completed but '$metadata' was not created."
}
if (-not (Test-Path $hashedDir)) {
    throw "kachina-builder gen completed but '$hashedDir' was not created."
}

Write-Host "Packing Kachina offline installer..."
Remove-Item $installer -Force -ErrorAction SilentlyContinue
& $builder pack -c $config -m $metadata -d $hashedDir -o $installer --icon $icon
if ($LASTEXITCODE -ne 0) {
    throw "kachina-builder pack for installer failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $installer)) {
    throw "kachina-builder pack for installer completed but '$installer' was not created."
}

Write-Host "Kachina installer created: $installer"
