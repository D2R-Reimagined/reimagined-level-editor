$ErrorActionPreference = 'Stop'
$scripts = $PSScriptRoot
foreach ($value in @('1.0.0', 'v1.2.3', '2.0.0-beta.1')) {
    $result = & "$scripts/Resolve-ReleaseVersion.ps1" -Version $value
    if (!$result.Tag.StartsWith('v')) { throw 'Version normalization failed.' }
}
foreach ($value in @('01.0.0', '1.0', '1.0.0-01', '1.0.0+metadata', '1.0.0;bad', '65535.0.0')) {
    $rejected = $false
    try { & "$scripts/Resolve-ReleaseVersion.ps1" -Version $value | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "Invalid version accepted: $value" }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('rle-release-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Invoke-Git { & git @args; if ($LASTEXITCODE -ne 0) { throw "Git failed: $args" } }
Push-Location $fixture
try {
    Invoke-Git init --quiet
    Invoke-Git config user.name 'Release test'
    Invoke-Git config user.email 'test@example.invalid'
    Invoke-Git -c commit.gpgsign=false commit --allow-empty -m 'Initial' --quiet
    & "$scripts/Generate-ReleaseNotes.ps1" -CurrentTag v1.0.0 -OutputPath first.md
    if ((Get-Content first.md -Raw) -notmatch 'Initial') { throw 'First release notes missing.' }
    Invoke-Git tag v1.0.0
    Invoke-Git -c commit.gpgsign=false commit --allow-empty -m 'unstructured message' --quiet
    $rejected = $false
    try { & "$scripts/Test-SemanticCommits.ps1" -BaseRef v1.0.0 } catch { $rejected = $true }
    if (!$rejected) { throw 'Non-semantic PR was accepted.' }
    Invoke-Git -c commit.gpgsign=false commit --allow-empty -m 'feat(editor)!: add scene tooling' --quiet
    Invoke-Git -c commit.gpgsign=false commit --allow-empty -m 'fix(save): retain backups' --quiet
    & "$scripts/Test-SemanticCommits.ps1" -BaseRef v1.0.0
    & "$scripts/Generate-ReleaseNotes.ps1" -CurrentTag v1.1.0 -OutputPath next.md
    $notes = Get-Content next.md -Raw
    foreach ($expected in @('Changes since v1.0.0', '## Breaking Changes', '## Features', '## Fixes', 'retain backups', 'unstructured message')) {
        if (!$notes.Contains($expected)) { throw "Missing release notes: $expected" }
    }
    if ($notes.Contains('- Initial')) { throw 'Previous release content leaked into new notes.' }
    Write-Host 'PASS: version validation, first release, semantic PR validation, breaking changes and incremental release notes.'
} finally { Pop-Location }
