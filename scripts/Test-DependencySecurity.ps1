[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$report = & dotnet list (Join-Path $root 'ShipTime4x4.sln') package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability check failed.' }
$audit = ($report -join [Environment]::NewLine) | ConvertFrom-Json
if (@($audit.logs | Where-Object { $_.level -match 'error|warning' }).Count) {
    throw 'Dependency advisory sources could not be checked reliably.'
}
$findings = @(
    foreach ($project in $audit.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package.vulnerabilities) { $package }
            }
        }
    }
)
if ($findings.Count) {
    $findings | ConvertTo-Json -Depth 10 | Write-Host
    throw 'Known vulnerable dependencies found. Update them before releasing.'
}
Write-Host 'No known vulnerabilities reported for resolved NuGet dependencies.'
