[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputPath)

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a publish path outside the artifacts folder: $output"
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }

dotnet restore (Join-Path $root 'ShipTime4x4.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
& (Join-Path $PSScriptRoot 'Test-DependencySecurity.ps1')
dotnet test (Join-Path $root 'ShipTime4x4.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
dotnet publish (Join-Path $root 'src\ShipTime4x4.Hotfolder\ShipTime4x4.Hotfolder.csproj') `
    -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'ReLabel publish failed.' }

$required = @(
    'ReLabel.exe',
    'pdfium.dll',
    'TesseractOCR.dll',
    'tessdata\eng.traineddata'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $relative))) {
        throw "Published runtime is missing $relative"
    }
}
$previousApplication = $env:RELABEL_TEST_APPLICATION
try {
    $env:RELABEL_TEST_APPLICATION = $output
    dotnet test (Join-Path $root 'tests\ShipTime4x4.Tests\ShipTime4x4.Tests.csproj') `
        -c Release --no-build --no-restore --filter 'FullyQualifiedName~SecurityTests|FullyQualifiedName~MultiPagePdfTests'
    if ($LASTEXITCODE -ne 0) { throw 'Published worker smoke tests failed.' }
}
finally { $env:RELABEL_TEST_APPLICATION = $previousApplication }
Write-Host "ReLabel release created at $output"
