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

In GitHub Actions, set the repository variable `ROCO_R2_PUBLIC_URL` to the R2 public URL prefix. The workflow appends `/RocoPilot.Install.exe` and adds that as the R2 source automatically.

When `ROCO_UPLOAD_TO_R2` is `true`, the workflow also uploads `/latest.json` to the same R2 public URL and injects that URL into `RocoPilot/appsettings.json` for release builds. The app still checks GitHub Releases first; it only reads this metadata file after the GitHub API request fails.

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
