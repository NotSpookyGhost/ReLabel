# ReLabel 2.4.6 - Print All

- Added **Print All** at the right of the list toolbar. It prints eligible Ready PDFs in the displayed newest/oldest-first order, using each label's existing settings and every page of multi-page files.
- Replaced **Add PDF** with drag and drop: drop one or more PDF files anywhere on the main window, including the list and preview. Originals stay untouched, duplicate filenames receive a number, and non-PDF items are ignored.
- Dropped files copy in the background and enter the existing import/security checks only after copying completes. Incoming-folder watching and Auto Print settings still apply.
- A highlighted drop-area overlay appears while supported PDFs hover over the main window, shows the PDF count, and disappears on drop or when the drag leaves/cancels.
- Print All starts without a confirmation by default. Enable **Confirm before printing all labels** under **Settings > App Settings** for a themed dialog asking whether to print all X labels; the count includes each page of multi-page PDFs.
- Batch progress appears in the bottom status bar. **Stop After File** finishes the current PDF and leaves the remaining files unprinted; jobs already sent to Windows cannot be recalled.
- Existing ambiguous-template prompts and optional scanner verification are retained. A print error or cancelled confirmation/verification stops the batch.
- Newly imported files are not added to an active batch. Items already printed, removed, unavailable, failed, or printing are skipped rather than reprinted.
- Print All is disabled on the Printed tab. Individual Print Again remains available outside a batch.
- Duplicate manual requests are blocked while printing, and Ready status is rechecked under the print lock to avoid races with Auto Print.
- The toolbar wraps when necessary to keep controls from overlapping at narrow widths.

Existing settings, folder paths, templates, and audit logging are preserved. The new Print All confirmation option defaults to off. The current tab remains selected after printing. Auto Print continues to operate independently when enabled.

Validation: **154 automated tests passed** locally. The GitHub release workflow also runs the test suite, published-worker smoke tests, and dependency security checks before packaging.

## Downloads and verification

- [Windows x64 installer](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.4.6/ReLabel-Setup-2.4.6.exe)
- [SHA-256 checksum](https://github.com/NotSpookyGhost/ReLabel/releases/download/v2.4.6/ReLabel-Setup-2.4.6.exe.sha256)

The release installer is built on GitHub and published from the attested workflow artifact. Verify its provenance using:

```powershell
gh attestation verify .\ReLabel-Setup-2.4.6.exe --repo NotSpookyGhost/ReLabel
```

Build attestation is not Windows Authenticode signing; SmartScreen may still show a warning. Existing settings and folder paths are preserved. Physical print-and-scan testing is still recommended.
