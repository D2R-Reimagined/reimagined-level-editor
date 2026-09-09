param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$value = $Version.Trim() -creplace '^v', ''
$number = '(0|[1-9][0-9]*)'
$identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
if ($value -cnotmatch "^$number\.$number\.$number(?:-($identifier(?:\.$identifier)*))?$") {
    throw 'Use MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-prerelease (no build metadata).'
}
$parts = ($value -split '-')[0].Split('.')
foreach ($part in $parts) {
    if ([long]$part -gt 65534) { throw 'Version components must be <= 65534 for Windows assembly versions.' }
}
[pscustomobject]@{ Version = $value; Tag = "v$value"; Prerelease = $value.Contains('-') }
