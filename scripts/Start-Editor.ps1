param(
    [string]$Preset,
    [string]$DataRoot,
    [string]$GrannyPath,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$published = Join-Path $projectRoot 'artifacts\app\D2RLevel.App.exe'
$editorArgs = @()
if ($Preset) { $editorArgs += @('--preset', (Resolve-Path -LiteralPath $Preset).Path) }
if ($DataRoot) { $editorArgs += @('--data', (Resolve-Path -LiteralPath $DataRoot).Path) }
if ($GrannyPath) { $editorArgs += @('--granny', (Resolve-Path -LiteralPath $GrannyPath).Path) }
if (Test-Path -LiteralPath $published) { & $published @editorArgs; exit $LASTEXITCODE }
& $DotNet run --project (Join-Path $projectRoot 'src\D2RLevel.App') -- @editorArgs
exit $LASTEXITCODE
