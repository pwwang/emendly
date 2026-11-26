# WinGet Packaging for Emendly

This directory holds the WinGet 1.6.0 multi-file manifests for `pwwang.Emendly` and a zero-dependency helper script to generate manifests for new releases.

## 1. Generate manifests for a new release

From the repository root, using a downloaded installer `.exe`:

```sh
python3 packaging/winget/update-manifest.py \
  --version 0.2.0 \
  --installer path/to/Emendly-v0.2.0-win-x64-setup.exe
```

Or using the SHA-256 hash from `SHA256SUMS.txt` on the GitHub Release:

```sh
python3 packaging/winget/update-manifest.py \
  --version 0.2.0 \
  --sha256 <64-char-hex-sha256>
```

Pass `--dry-run` to preview the generated YAML without writing files.

## 2. Validate locally on Windows

```powershell
winget validate --manifest packaging\winget\manifests\p\pwwang\Emendly\0.2.0
```

To test a local install from the manifest before submitting (requires local manifests enabled once with `winget settings --enable LocalManifestFiles`):

```powershell
winget install --manifest packaging\winget\manifests\p\pwwang\Emendly\0.2.0
```

## 3. Submit to `microsoft/winget-pkgs`

Install the official WinGet Create CLI (`winget install Microsoft.WingetCreate`), then submit:

### First submission (or submitting a generated manifest directory)

```powershell
wingetcreate submit --token <github-pat> packaging\winget\manifests\p\pwwang\Emendly\0.2.0
```

### Subsequent version updates (automated directly from release URL)

```powershell
wingetcreate update pwwang.Emendly `
  --version 0.2.0 `
  --urls https://github.com/pwwang/emendly/releases/download/v0.2.0/Emendly-v0.2.0-win-x64-setup.exe `
  --submit
```
