param(
    [Parameter(Mandatory=$true)]
    [string]$Version,

    [Parameter(Mandatory=$true)]
    [string]$PackageJsonPath
)

$content = Get-Content $PackageJsonPath -Raw
$updated = $content -replace '("version"\s*:\s*)"[^"]*"', "`$1`"$Version`""
if ($updated -ne $content) {
    Set-Content -Path $PackageJsonPath -Value $updated -NoNewline
}
