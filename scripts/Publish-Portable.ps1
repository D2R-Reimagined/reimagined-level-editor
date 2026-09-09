param([string]$DotNet = 'dotnet', [switch]$IncludeGrannyRuntime = $true)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $repo "artifacts/releases/$stamp"
$package = Join-Path $destination 'Reimagined-Level-Editor-win-x64'
New-Item -ItemType Directory -Path $package -Force | Out-Null
if ($IncludeGrannyRuntime -and !(Test-Path -LiteralPath (Join-Path $repo 'src/D2RLevel.App/Native/granny2.dll'))) { throw 'Supply your licensed Native/granny2.dll before including the runtime.' }
& $DotNet publish (Join-Path $repo 'src/D2RLevel.App/D2RLevel.App.csproj') -c Release -r win-x64 --self-contained true '-p:PublishProfile=Portable' '-p:DebugType=none' '-p:DebugSymbols=false' "-p:IncludeGrannyRuntime=$($IncludeGrannyRuntime.IsPresent.ToString().ToLowerInvariant())" "-p:BaseOutputPath=$destination/build/" -o $package
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no ZIP was created.' }
Copy-Item -LiteralPath (Join-Path $repo 'docs/TESTER-README.txt') -Destination (Join-Path $package 'README.txt')
Copy-Item -LiteralPath (Join-Path $repo 'vendor/lslib/LICENSE') -Destination (Join-Path $package 'LSLib-LICENSE.txt')
# Include package-supplied licenses and metadata without shipping build caches or user settings.
$assets = Get-Content (Join-Path $repo 'src/D2RLevel.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$notices = [System.Text.StringBuilder]::new()
[void]$notices.AppendLine('Third-party components bundled in Reimagined Level Editor')
[void]$notices.AppendLine('LSLib: see LSLib-LICENSE.txt. Granny: see Native/NOTICE.txt.')
foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $path = Join-Path $folder $library.Value.path
        if (!(Test-Path -LiteralPath $path)) { continue }
        [void]$notices.AppendLine("`n=== $($library.Name) ===")
        Get-ChildItem -LiteralPath $path -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING)' -or $_.Extension -eq '.nuspec' } | ForEach-Object {
            [void]$notices.AppendLine((Get-Content -LiteralPath $_.FullName -Raw))
        }
        break
    }
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    foreach ($runtime in $framework.downloadDependencies) {
        $version = ($runtime.version.Trim('[', ']') -split ',')[0].Trim()
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $path = Join-Path $folder ($runtime.name.ToLowerInvariant() + '/' + $version)
            if (!(Test-Path -LiteralPath $path)) { continue }
            [void]$notices.AppendLine("`n=== $($runtime.name) $version ===")
            Get-ChildItem -LiteralPath $path -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' } | ForEach-Object {
                [void]$notices.AppendLine((Get-Content -LiteralPath $_.FullName -Raw))
            }
        }
    }
}
$notices.ToString() | Set-Content (Join-Path $package 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
$zip = Join-Path $destination 'Reimagined-Level-Editor-win-x64.zip'
Compress-Archive -Path $package -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ascii
Write-Host "Package: $zip"
if ($IncludeGrannyRuntime) { Write-Host 'Package contains Granny. Verify your redistribution rights before sharing it.' }
else { Write-Host 'Granny is excluded. Users supply their own compatible runtime for model rendering.' }
