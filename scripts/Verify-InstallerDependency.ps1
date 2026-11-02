[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw 'The Visual C++ redistributable does not have a valid trusted signature.'
}
$publisher = $signature.SignerCertificate.GetNameInfo(
    [Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
if ($publisher -cne 'Microsoft Corporation' -or
    $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
    throw 'The Visual C++ redistributable was not signed by Microsoft Corporation.'
}
$hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
Write-Host "Verified Microsoft redistributable: SHA256 $($hash.Hash)"
