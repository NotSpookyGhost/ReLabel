# Security

## ReLabel 2.4.5 safeguards

- PDFium rendering and Tesseract OCR execute in a low-integrity Windows child process with privileges stripped and the Administrators SID disabled. A Windows job limits it to one process and 1 GiB committed process memory, and terminates it when its owner closes. Failure to establish this boundary stops PDF processing; there is no in-process fallback.
- Only bounded PDF bytes and a small operation/limit header enter the worker. File paths are not sent to it. Only bounded raw pixels and text return; the application does not decode a worker-supplied image format.
- App Settings expose PDF size, page count, page pixel budget, and worker deadline limits. Defaults: 50 MiB, 100 pages, 40 megapixels, 45 seconds per worker operation. Maximum selectable values: 100 MiB, 500 pages, 40 megapixels, 120 seconds. One native operation and one print-preparation job run at a time; the import queue is bounded at 128. Combined ZPL jobs are capped at 128 MiB.
- Oversized or invalid incoming PDFs rejected during inspection are moved into the per-user application-data Quarantine directory, with an audit entry. Quarantine is local, is not automatically deleted, and can contain shipping information. Review files before moving them back.
- Template imports enforce archive, metadata, zone, feature, and sample limits. The application inspects the sample before committing the template and regenerates matching features. Invalid saved catalog entries are skipped and the catalog is backed up for recovery.
- Managed-file cleanup requires a reachable directory and repeated missing-file observations at least five seconds apart. Inaccessible directories reset the confirmation rather than causing archive deletion. Genuine removals can take roughly 10–20 seconds to reconcile.
- Runtime/rendering packages are updated and NuGet dependencies are locked. Release builds reject known vulnerable NuGet packages and an untrusted/non-Microsoft Visual C++ redistributable.

## Boundaries and operational guidance

Low integrity limits writes to normal user files; it is **not** an AppContainer, a network firewall, or a promise that a native parser is invulnerable. The worker still has read access allowed to the Windows user and may have network access. Run ReLabel as a normal user, not an administrator. Restrict incoming-folder write access to trusted staff and machines; do not expose an anonymous writable share to an untrusted network.

PDF deadlines apply to each worker operation, not the total lifetime of a multi-page shipment. Processing-limit rejection may require splitting a large shipment into smaller PDFs. Existing cached output and ordinary configuration are retained across upgrades; print caches are versioned to invalidate pre-hardening results.

Template PDFs, archives, quarantine files, and audit filenames may contain personal information. Windows account permissions, disk encryption, retention policies, and network-share access controls remain the operator's responsibility.

NuGet advisory checks do not discover every vulnerability in vendored native components. Keep Windows, printer drivers, runtime packages, and native dependencies current. Automated regression tests do not replace a physical print/scan acceptance test.

## Release authenticity

The GitHub release workflow builds and attests installers using GitHub's Sigstore-based provenance service. It uses pinned action commits, limited token permissions, and no persisted checkout credentials. An attestation only exists after a successful GitHub workflow run; a local build is not attested.

```powershell
gh attestation verify .\ReLabel-Setup-2.4.5.exe --repo NotSpookyGhost/ReLabel
```

Verify the repository and workflow identity in the result. The adjacent SHA-256 file can detect corruption but is not an independent authenticity guarantee. Neither checksums nor GitHub attestations are Windows Authenticode certificates. SmartScreen warnings may remain.

Free Authenticode signing through [SignPath Foundation](https://signpath.org/) requires project approval and owner configuration. No paid certificate or external signing enrollment is included in this patch.

## Reporting

Please do not post real shipping PDFs, addresses, credentials, or working exploits in public issues. Use GitHub private vulnerability reporting if it is enabled for this repository; otherwise contact the maintainer privately before sharing sensitive details.
