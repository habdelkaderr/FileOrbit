# Security

## Reporting a vulnerability

Please report security problems privately via **Security → Report a vulnerability** on this
repository (GitHub private vulnerability reporting). Do not open a public issue for them.

## Verifying a download

Official downloads are only published on this repository's
[Releases](https://github.com/habdelkaderr/FileOrbit/releases) page. They are built by GitHub
Actions from the tagged source code ([release workflow](.github/workflows/release.yml)), not on
anyone's PC.

Before running a downloaded zip you can check that it is genuine:

1. **Checksum:** compare the SHA-256 shown on the release page (and in the `.sha256` file) with your
   download:

   ```powershell
   Get-FileHash .\FileOrbit-0.1.0-win-x64.zip -Algorithm SHA256
   ```

2. **Build provenance** (proves the file was built by this repository's workflow from a specific
   commit), using the [GitHub CLI](https://cli.github.com/):

   ```powershell
   gh attestation verify .\FileOrbit-0.1.0-win-x64.zip --repo habdelkaderr/FileOrbit
   ```

If either check fails, do not run the file.

## What protects the code

- Only the repository owner can change `main`. Branch rules block force pushes and deletion, and
  changes from others arrive only as pull requests that must pass CI.
- NuGet dependencies are pinned by version **and content hash** (`packages.lock.json`). CI restores
  in locked mode, so a swapped or tampered package fails the build.
- Dependabot reports vulnerable dependencies, and CodeQL scans the code on every change and weekly.
- Workflows run with read-only permissions, except the release job, which may only create releases
  and sign attestations.

## What the program does on your PC

FileOrbit processes files locally and has no network code: it never uploads files and has no
telemetry. It writes only:

- converted files, next to the original (or a folder you choose); the original is never modified,
- settings, history and logs in `%LocalAppData%\FileOrbit`,
- the per-user Explorer menu entry and, if you enable it, the start-with-Windows entry
  (under `HKEY_CURRENT_USER`; removable from Settings).

It starts external programs only if you installed them yourself (Microsoft Office, LibreOffice,
FFmpeg, 7-Zip, Ghostscript) and never downloads or installs software. See the
[Security and privacy](README.md#security-and-privacy) section of the README for details.
