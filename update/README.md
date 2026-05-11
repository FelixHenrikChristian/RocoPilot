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

Use an existing publish directory:

```powershell
.\update\build-kachina.ps1 -SkipPublish -PublishDir .\build\publish
```

Add a Cloudflare/R2 source when you have the final URL:

```powershell
.\update\build-kachina.ps1 -CloudflareInstallerUrl "https://example.r2.dev/RocoPilot.Install.exe"
```

You can also set `ROCO_KACHINA_CLOUDFLARE_URL` instead of passing the parameter.

Generated files are intentionally ignored by git:

- `RocoPilot.update.exe`
- `RocoPilot.Install.exe`
- `metadata.json`
- `hashed/`
- `publish/`
- `kachina-builder.exe`
