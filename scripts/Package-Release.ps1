# Builds the portable ZIP and the Velopack installer/update feed for one version.
# Output: artifacts/release/<version>/ (portable ZIP + .sha256) and artifacts/release/<version>/velopack/
# (Setup.exe, updatable Portable.zip, full .nupkg and releases.win.json that installed editors poll).
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$DotNet = 'dotnet',
    [switch]$IncludeGrannyRuntime = $true,
    [string]$RepositoryUrl = 'https://github.com/D2R-Reimagined/reimagined-level-editor'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$Version = (& (Join-Path $repo '.github/scripts/Resolve-ReleaseVersion.ps1') -Version $Version).Version
$destination = Join-Path $repo "artifacts/release/$Version"
& "$PSScriptRoot/Publish-Portable.ps1" -DotNet $DotNet -IncludeGrannyRuntime:$IncludeGrannyRuntime -Version $Version -UpdateRepositoryUrl $RepositoryUrl -OutputDirectory $destination
$package = Join-Path $destination 'Reimagined-Level-Editor-win-x64'
if (!(Test-Path -LiteralPath (Join-Path $package 'D2RLevel.App.exe'))) { throw "Published editor not found in $package" }

[xml]$project = Get-Content (Join-Path $repo 'src/D2RLevel.App/D2RLevel.App.csproj')
$toolVersion = @($project.Project.PropertyGroup.VelopackVersion | Where-Object { $_ })[0]
if (!$toolVersion) { throw 'VelopackVersion is missing from D2RLevel.App.csproj.' }
$toolPath = Join-Path $repo "artifacts/tools/vpk-$toolVersion"
$vpk = Join-Path $toolPath 'vpk.exe'
if (!(Test-Path -LiteralPath $vpk)) {
    & $DotNet tool install vpk --version $toolVersion --tool-path $toolPath --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool installation failed.' }
}
# vpk is a framework-dependent tool; point it at the SDK that installed it.
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $DotNet).Source

$output = Join-Path $destination 'velopack'
& $vpk --yes pack --packId D2RReimagined.LevelEditor --packVersion $Version --packDir $package --mainExe D2RLevel.App.exe `
    --packTitle 'Reimagined Level Editor' --packAuthors 'D2R Reimagined' --runtime win-x64 --channel win `
    --icon (Join-Path $repo 'src/D2RLevel.App/Assets/ReimaginedLevelEditor.ico') --outputDir $output
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
foreach ($required in @('releases.win.json', 'D2RReimagined.LevelEditor-win-Setup.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $output $required))) { throw "Velopack output is missing $required" }
}
Write-Host "Velopack release ready: $output"
