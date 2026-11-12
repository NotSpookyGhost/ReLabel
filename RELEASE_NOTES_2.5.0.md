# ReLabel 2.5.0 - Multi-printer Support

- Added selectable ZPL / ZPL II, Windows-driver, EPL2, and TSPL / TSPL2 output.
- Preserved existing Zebra output and settings. Upgrades keep ZPL selected and retain configured folder paths.
- Added native EPL2/TSPL media and gap/black-mark settings. Density/speed overrides are optional and disabled by default; their units follow the selected language.
- Added Windows-driver actual-size monochrome printing, resolution checks, and printable-area checks to reject detected clipping.
- Preserved every page of multi-label PDFs for all output modes and cached reprints. Old prepared caches rebuild automatically.
- Routed Print Label, Print Again, Print All, Auto Print, and Print Quality Test through the selected output mode.
- Added mode-specific help and scrollable printer settings.
- Included the empty-list rounded plus import button and concise drag-and-drop/hotfolder guidance from the local 2.4.7 build.

## Before production use

EPL2 and TSPL are experimental; Windows-driver compatibility depends on the exact model and driver. These changes do not certify every printer from a manufacturer. Match physical label size, DPI, language, and media. Print a quality test and physically scan representative carrier labels, including multi-page shipments, before production use. No physical printer or scanner testing is claimed for this build.

Native CPCL, ESC/POS, SBPL, IPL/DPL, TPCL, and Brother raster are not implemented. Suitable vendor Windows drivers or supported emulation modes may provide compatibility.

## Downloads and verification

- [Windows x64 installer](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.5.0/ReLabel-Setup-2.5.0.exe)
- [SHA-256 checksum](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.5.0/ReLabel-Setup-2.5.0.exe.sha256)

The release installer is built by the repository's Verified installer GitHub workflow and published as the exact attested artifact. Verify it with:

```powershell
gh attestation verify .\ReLabel-Setup-2.5.0.exe --repo NotSpookyGhost/ReLabel
```

GitHub build attestation verifies provenance, not printer compatibility or Windows Authenticode signing. The installer remains unsigned for Windows, and SmartScreen may display a warning.
