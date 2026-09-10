param([Parameter(Mandatory)][string]$BaseRef, [string]$HeadRef = 'HEAD')
$ErrorActionPreference = 'Stop'
# Check the actual PR commits, not GitHub's generated merge commit.
$subjects = @(git log "$BaseRef..$HeadRef" '--format=%s' --no-merges)
if ($LASTEXITCODE -ne 0) { throw 'Unable to read PR commit range.' }
$pattern = '^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^\r\n)]+\))?!?: \S.*$'
if (@($subjects | Where-Object { $_ -cmatch $pattern }).Count -eq 0) {
    throw 'Include at least one semantic commit: type(scope): description. Types: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert.'
}
Write-Host 'Semantic commit check passed.'