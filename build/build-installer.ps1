param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$buildDir = $PSScriptRoot
$repoRoot = Split-Path $buildDir -Parent
$toolsProject = Join-Path $buildDir "MicaSetup.Tools.csproj"
$project = Join-Path $repoRoot "RocoPilot\RocoPilot.csproj"
$publishDir = Join-Path $buildDir "publish"
$package7z = Join-Path $buildDir "publish.7z"
$config = Join-Path $buildDir "micasetup.json"
$templateDir = Join-Path $buildDir ".template"
$templateWorkDir = Join-Path $templateDir "default"
$templateArchive = Join-Path $templateDir "default.7z"
$distDir = Join-Path $buildDir ".dist"

function ConvertTo-CSharpStringLiteral {
    param([string]$Value)

    return $Value.Replace("\", "\\").Replace('"', '\"')
}

function Set-AssemblyAttribute {
    param(
        [string]$Path,
        [string]$Name,
        [string]$Value
    )

    $literal = ConvertTo-CSharpStringLiteral $Value
    $pattern = '\[assembly:\s*' + [regex]::Escape($Name) + '\("([^"]*)"\)\]'
    $replacement = '[assembly: ' + $Name + '("' + $literal + '")]'
    $content = Get-Content $Path -Raw
    $content = [regex]::Replace(
        $content,
        $pattern,
        [System.Text.RegularExpressions.MatchEvaluator] { param($match) $replacement })
    Set-Content -Path $Path -Value $content -Encoding UTF8
}

Write-Host "Restoring MicaSetup.Tools..."
dotnet restore $toolsProject
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

$nugetLocals = dotnet nuget locals global-packages --list
$globalPackages = ($nugetLocals -split ":\s*", 2 | Select-Object -Last 1).Trim()
$micaTools = Join-Path $globalPackages "micasetup.tools\2.5.0\build"
$sevenZip = Join-Path $micaTools "bin\7z.exe"
$makeMica = Join-Path $micaTools "makemica.exe"

if (-not (Test-Path $sevenZip)) {
    throw "7z.exe was not found at '$sevenZip'."
}

if (-not (Test-Path $makeMica)) {
    throw "makemica.exe was not found at '$makeMica'."
}

$configData = Get-Content $config -Raw | ConvertFrom-Json
$sourceTemplate = Join-Path $micaTools "template\default.7z"
if (-not (Test-Path $sourceTemplate)) {
    throw "MicaSetup default template was not found at '$sourceTemplate'."
}

Remove-Item $templateDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $templateWorkDir | Out-Null

Write-Host "Preparing MicaSetup template metadata..."
& $sevenZip x $sourceTemplate "-o$templateWorkDir" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Template extraction failed with exit code $LASTEXITCODE."
}

$appName = [string]$configData.AppName
$publisher = [string]$configData.Publisher
$setupProgram = Join-Path $templateWorkDir "Program.cs"
$uninstProgram = Join-Path $templateWorkDir "Program.un.cs"

Set-AssemblyAttribute $setupProgram "AssemblyTitle" "$appName Setup"
Set-AssemblyAttribute $setupProgram "AssemblyProduct" $appName
Set-AssemblyAttribute $setupProgram "AssemblyDescription" "$appName Setup"
Set-AssemblyAttribute $setupProgram "AssemblyCompany" $publisher
Set-AssemblyAttribute $setupProgram "AssemblyCopyright" "Copyright (c) $publisher."

Set-AssemblyAttribute $uninstProgram "AssemblyTitle" "$appName Uninst"
Set-AssemblyAttribute $uninstProgram "AssemblyProduct" $appName
Set-AssemblyAttribute $uninstProgram "AssemblyDescription" "$appName Uninst"
Set-AssemblyAttribute $uninstProgram "AssemblyCompany" $publisher
Set-AssemblyAttribute $uninstProgram "AssemblyCopyright" "Copyright (c) $publisher."

& $sevenZip a $templateArchive "$templateWorkDir\*" -t7z -mx=5 -mf=BCJ2 -r -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Template packing failed with exit code $LASTEXITCODE."
}

Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $package7z -Force -ErrorAction SilentlyContinue
Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $buildDir -Filter "RocoPilot-Setup-v*.exe" -File -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Publishing RocoPilot ($Runtime)..."
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

Write-Host "Packing publish directory..."
& $sevenZip a $package7z "$publishDir\*" -t7z -mx=5 -mf=BCJ2 -r -y
if ($LASTEXITCODE -ne 0) {
    throw "7z failed with exit code $LASTEXITCODE."
}

Write-Host "Building MicaSetup installer..."
Push-Location $buildDir
try {
    & $makeMica $config
    if ($LASTEXITCODE -ne 0) {
        throw "makemica failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$installer = Get-ChildItem $buildDir -Filter "RocoPilot-Setup-v*.exe" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $installer) {
    throw "Installer output was not created."
}

Write-Host "Installer created: $($installer.FullName)"
