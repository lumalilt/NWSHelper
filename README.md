# NWS Helper

Public shell repository for NWS Helper.

This repository is intended to contain the public GUI, CLI, packaging scripts, release automation, and public-facing documentation. The proprietary `NWSHelper.Core` source stays in the private source-of-truth repository and is consumed here as a versioned binary package.

## Current bootstrap state

- `NWSHelper.Gui` and `NWSHelper.Cli` are present in this repo and default to package-backed Core consumption.
- Stable `NWSHelper.Core` packages are intended to come from `NuGet.org`.
- Local bootstrap builds can still point at a local packed artifact when you want to test an unpublished Core package.

## Local bootstrap build

From this repo, the default build restores the exact stable `NWSHelper.Core` version pinned in `version.json`.

The public automation treats that pinned version's major and minor as the currently allowed compatibility band. Patch updates in the same band can be adopted automatically after the published package has been validated against the public repo and the pin has been updated.

To test against a local packed Core artifact instead, build with:

```powershell
dotnet build .\NWSHelper.Public.slnx -p:CorePackageVersion=1.0.9-local -p:CorePackageSource="C:\path\to\packed-core-feed"
```

If you are using the published stable package, you do not need `CorePackageSource`.

## Included projects

- `NWSHelper.Gui`
- `NWSHelper.Cli`
- `scripts/` for public packaging and release support
- `version.json` for public app versioning plus the exact pinned `corePackageVersion`

## Release artifacts

The public release workflow is intended to publish:

- Windows installer (`.exe`)
- portable GUI and CLI zip archives
- `update-metadata.json`
- `checksums.sha256`
- `appcast.xml` and `appcast.xml.signature` on stable releases
- MSIX only when explicitly enabled for the direct-download channel or when Store packaging is being exercised

## Local self-signed MSIX

To build a repeatable locally signed MSIX for local MSIX-based install testing, use `scripts/build-local-self-signed-msix.ps1`.

The helper script will:

- create or reuse a self-signed certificate whose subject matches the MSIX publisher
- export that certificate to a PFX for the existing MSIX packaging script
- run `dotnet publish` for the Windows GUI target by default
- call `scripts/package-msix.ps1` with signing enabled
- generate `resources.pri` from the staged manifest and MSIX asset set before `makeappx pack` so Windows can resolve the unplated AppList icon qualifiers correctly
- optionally import the certificate into `CurrentUser\TrustedPeople` and `CurrentUser\Root`
- optionally install the resulting MSIX locally
- launch the installed app by default after install so local install checks immediately exercise the packaged app
- let you opt out of the post-install launch with `-SkipLaunchAfterInstall`
- optionally rebuild Explorer's icon cache with `-RefreshShellIconCache`

When you use `-InstallPackage`, the helper tries `Add-AppxPackage` after importing the certificate into the current user stores. If that direct install fails and the terminal is elevated, the helper imports the certificate into `LocalMachine\TrustedPeople` and `LocalMachine\Root` and retries automatically. If the direct install fails in a non-elevated terminal, re-run the helper as Administrator so it can perform the LocalMachine fallback path.

When you do not pass `-Version`, the helper derives an MSIX-safe timestamped local version from `version.json`. It keeps the `major` and `minor` values, combines the base `patch` with the current day-of-year for the third segment, and uses local `HHmm` for the revision. That produces a time-based local version that stays within MSIX's four-segment numeric limit. Pass `-Version` explicitly if you need a different local test version.

Example: build a local self-signed MSIX and trust the signing certificate for local install testing.

```powershell
./scripts/build-local-self-signed-msix.ps1 `
	-CertificatePassword 'local-test-password' `
	-ImportCertificateToTrustedPeople
```

When you use `-RefreshShellIconCache`, the helper prompts before restarting Windows Explorer to rebuild the shell icon cache. That restart can briefly flash the screen, close open File Explorer windows, and cause shell-hosted UI to redraw or restart.

Example: build, trust, install, and prompt to rebuild the Explorer icon cache.

```powershell
./scripts/build-local-self-signed-msix.ps1 `
	-CertificatePassword 'local-test-password' `
	-InstallPackage `
	-RefreshShellIconCache
```

Example: build, trust, and install the MSIX so the installed app launches immediately.

```powershell
./scripts/build-local-self-signed-msix.ps1 `
	-CertificatePassword 'local-test-password' `
	-InstallPackage
```

Example: build, trust, install, and do not launch the installed app.

```powershell
./scripts/build-local-self-signed-msix.ps1 `
	-CertificatePassword 'local-test-password' `
	-InstallPackage `
	-SkipLaunchAfterInstall
```

Use `-ImportCertificateToTrustedPeople` when you want to prime current-user trust without installing. Use `-SkipPublish` when you already have a publish directory from a previous run and want to reuse it. Use `-ForceNewCertificate` when you want to rotate the local self-signed PFX instead of reusing the newest matching certificate from `CurrentUser\My`.

## Support and security

- See `SUPPORT.md` for issue routing, support expectations, and release troubleshooting guidance.
- See `SECURITY.md` for how to report vulnerabilities without posting them publicly.
