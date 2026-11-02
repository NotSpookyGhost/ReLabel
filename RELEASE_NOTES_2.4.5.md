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

The initial release installer was built and tested locally. It is unsigned and has no GitHub build attestation; Windows SmartScreen may show a warning. The new GitHub workflow creates separately attested workflow artifacts. No paid signing service is required or configured.

## Validation

- 121 automated tests passed.
- 18 targeted security and multi-page PDF smoke tests passed against the published application.
- No known NuGet vulnerabilities were reported at packaging time.
- The bundled Visual C++ redistributable's Microsoft signature and the final installer checksum were verified.
