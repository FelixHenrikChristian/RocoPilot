# RocoPilot Kachina Package

This folder builds the Kachina updater and offline installer for RocoPilot.

Place `kachina-builder.exe` from the Kachina release in this folder before running the script. GitHub Actions downloads it during release builds, so the binary is not committed.

Default source:

```powershell
https://github.com/FelixHenrikChristian/RocoPilot/releases/latest/download/RocoPilot.Install.exe
```

Build both updater and offline installer:

```powershell
.\update\build-kachina.ps1
```

Build only the updater:

```powershell
.\update\build-kachina.ps1 -UpdaterOnly
```

Build the MicaSetup installer with in-app update support:

```powershell
.\update\build-kachina.ps1 -UpdaterOnly
.\build\build-installer.ps1 -RequireUpdater
```

Use an existing publish directory:

```powershell
.\update\build-kachina.ps1 -SkipPublish -PublishDir .\build\publish
```

Reuse an already built updater when packing the offline installer:

```powershell
.\update\build-kachina.ps1 -SkipPublish -PublishDir .\build\publish -SkipUpdaterBuild
```

Add a Cloudflare/R2 source when you have the final URL:

```powershell
.\update\build-kachina.ps1 -CloudflareInstallerUrl "https://example.r2.dev/RocoPilot.Install.exe"
```

You can also set `ROCO_KACHINA_CLOUDFLARE_URL` instead of passing the parameter.

GitHub Actions reads the release version from `RocoPilot/RocoPilot.csproj`. Manual release runs do not take a separate version input; tag-triggered release runs still verify that the `v*` tag matches the project version.

The release workflow always uploads release assets to Cloudflare R2:

- `RocoPilot.Install.exe`
- `RocoPilot-Setup.exe`
- `latest.json`

The fixed R2 public URL is:

```text
https://pub-749777b9646245b7bc1b15efee2b9b24.r2.dev
```

The app still checks GitHub Releases first. If the GitHub API request fails, it reads update metadata from the fixed fallback URL in `RocoPilot/appsettings.json`:

```text
https://pub-749777b9646245b7bc1b15efee2b9b24.r2.dev/latest.json
```

If the R2 public domain, account, or bucket changes, update both `.github/workflows/release.yml` and `RocoPilot/appsettings.json`.

`latest.json` uses the same fields consumed from the GitHub release API:

```json
{
  "tag_name": "v0.2.4",
  "name": "RocoPilot 0.2.4",
  "body": "Release notes in Markdown",
  "published_at": "2026-06-08T00:00:00Z",
  "html_url": "https://github.com/FelixHenrikChristian/RocoPilot/releases/tag/v0.2.4"
}
```

Generated files are intentionally ignored by git:

- `RocoPilot.update.exe`
- `RocoPilot.Install.exe`
- `metadata.json`
- `hashed/`
- `publish/`
- `kachina-builder.exe`
