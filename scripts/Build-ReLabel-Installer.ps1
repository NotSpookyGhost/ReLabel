[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'artifacts\publish'
$runtime = Join-Path $root 'artifacts\dependencies\VC_redist.x64.exe'
$script = Join-Path $PSScriptRoot 'ReLabel.iss'
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\ShipTime4x4.Hotfolder\ShipTime4x4.Hotfolder.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The ReLabel project version could not be read.' }
$output = Join-Path $root "artifacts\installer\ReLabel-Setup-$version.exe"
$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Inno Setup 6 compiler was not found.' }
foreach ($required in @(
    (Join-Path $publish 'ReLabel.exe'),
    (Join-Path $publish 'pdfium.dll'),
    (Join-Path $publish 'tessdata\eng.traineddata'),
    $runtime,
    $script
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required installer input is missing: $required" }
}

New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
& $compiler $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
if (-not (Test-Path -LiteralPath $output)) { throw 'Setup executable was not created.' }

$item = Get-Item -LiteralPath $output
$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
Write-Host "Installer: $($item.FullName)"
Write-Host "Size: $($item.Length) bytes"
Write-Host "SHA256: $($hash.Hash)"
