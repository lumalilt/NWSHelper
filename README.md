# NWS Helper

Public shell repository for NWS Helper.

This repository is intended to contain the public GUI, CLI, packaging scripts, release automation, and public-facing documentation. The proprietary `NWSHelper.Core` source stays in the private source-of-truth repository and is consumed here as a versioned binary package.

## Current bootstrap state

- `NWSHelper.Gui` and `NWSHelper.Cli` are present in this repo and default to package-backed Core consumption.
- Stable `NWSHelper.Core` packages are intended to come from `NuGet.org`.
- Local bootstrap builds can still point at a local packed artifact when you want to test an unpublished Core package.

## Local bootstrap build

From this repo, the default build restores the stable `NWSHelper.Core` version from `NuGet.org` that matches `version.json`.

To test against a local packed Core artifact instead, build with:

```powershell
dotnet build .\NWSHelper.Public.slnx -p:CorePackageVersion=1.0.9-local -p:CorePackageSource="C:\path\to\packed-core-feed"
```

If you are using the published stable package, you do not need `CorePackageSource`.

## Included projects

- `NWSHelper.Gui`
- `NWSHelper.Cli`
- `scripts/` for public packaging and release support

## Release artifacts

The public release workflow is intended to publish:

- Windows installer (`.exe`)
- portable GUI and CLI zip archives
- `update-metadata.json`
- `checksums.sha256`
- `appcast.xml` and `appcast.xml.signature` on stable releases
- MSIX only when explicitly enabled for the direct-download channel or when Store packaging is being exercised

## Support and security

- See `SUPPORT.md` for issue routing, support expectations, and release troubleshooting guidance.
- See `SECURITY.md` for how to report vulnerabilities without posting them publicly.
