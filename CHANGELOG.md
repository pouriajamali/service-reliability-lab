# Changelog

## 0.1.2-preview.1

- Reject dirty/untracked source and non-allowlisted, symlink or generated tracked
  files before packaging; package exact Git bytes with a source commit/tree and
  per-file manifest. Fixed stored ZIP entries reproduce across operating systems.
- Add bounded packaging safety checks and actual member-byte validation to both
  Windows and Linux CI. Preserve the original runtime, 48 checks and four traces.
- Add direct offline-HTML instructions and public-key/fingerprint, signature and
  checksum verification commands. No new executable binaries or hosted changes.
- Preserve all earlier release assets and signatures.

## Unreleased - documentation

- Clarify the source quick start and expected result, and link focused contribution guidance. Runtime behavior, release assets and recorded measurements are unchanged.


## 0.1.1-preview.1

- Raised small labels, metadata and diagnostic text to at least 15 px.
- Stacked the mobile dependency flow without changing its nodes or replay behavior.
- Preserved the original 0.1.0 release files and signatures.

## 0.1.0-preview.1

- Added a C# read-dependency policy with bounded retries and waiting timeouts.
- Added a monotonic circuit deadline, single recovery probe and epoch-protected
  completions.
- Added cancellation, payload-bound, concurrency and telemetry checks.
- Added four reproducible scenario traces and a self-contained browser replay.
- Added a Windows/Linux verification workflow. Runtime and publication results
  belong to the release receipt, not to this source changelog.
