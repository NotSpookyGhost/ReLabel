# ReLabel 2.4.5 — Security hardening

- Updated to .NET 10.0.14, PDFtoImage 5.4.0, PDFium 158.0.8125, and SkiaSharp 4.154.0.
- Isolated PDF rendering/OCR in a restricted, memory-limited Windows worker with processing deadlines.
- Added configurable PDF size/page/pixel/time limits, bounded preparation/import queues, and quarantine for rejected incoming files.
- Rejected malformed template metadata before persistence and recovered safely from invalid saved template entries.
- Preserved labels and archives during inaccessible/network-folder outages; genuine deletion requires confirmation.
- Added locked dependency restores, advisory checks, scheduled CI, and dependency-update automation.
- Verified the trusted Microsoft signature on the Visual C++ installer before packaging.
- Added a GitHub release workflow for free build attestations and SHA-256 installer checksums. This does not add Windows Authenticode signing.

Existing configuration and folder paths are preserved. Large PDFs may need to be split or have their processing limits adjusted in App Settings. Quarantined files remain local for review. Physical printer/scan acceptance testing is still recommended.

## Downloads

- [Windows x64 installer](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.4.5/ReLabel-Setup-2.4.5.exe)
- [SHA-256 checksum](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.4.5/ReLabel-Setup-2.4.5.exe.sha256)

The release installer now comes from the [Verified installer workflow](https://github.com/NotSpookyGhost/ReLabel/actions/runs/41495300297) and has a verified GitHub build attestation for the v2.4.5 tag and source commit e1a30aa8544e8f1164c34d0f08058c23c9909f05. It replaces the earlier locally built installer; download the matching updated SHA-256 checksum. Windows Authenticode signing is still not configured, so SmartScreen may show a warning. No paid signing service is required.

## Validation

- 121 automated tests passed.
- 18 targeted security and multi-page PDF smoke tests passed against the published application.
- No known NuGet vulnerabilities were reported at packaging time.
- The bundled Visual C++ redistributable's Microsoft signature and the final installer checksum were verified.
