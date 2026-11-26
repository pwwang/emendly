# Windows Code Signing Guide

`.github/workflows/release.yml` automatically signs `Emendly.App.exe`, `emendly-engine.exe`, and `Emendly-v<version>-win-x64-setup.exe`, and publishes `SHA256SUMS.txt` alongside every GitHub Release.

Signing is **optional**: if no signing secrets are configured in the repository, the release workflow emits a notice and ships unsigned binaries without failing.

When secrets are present, the workflow checks them in this order:
1. **Option A — Azure Artifact Signing** (preferred; active when all 6 `AZURE_*` secrets are set)
2. **Option B — Traditional PFX Certificate** (active when both `WINDOWS_SIGN_PFX_*` secrets are set)

---

## Option A: Azure Artifact Signing (Recommended)

Microsoft's Azure Artifact Signing (formerly Trusted Signing) signs with short-lived, cloud-hosted certificates backed by Microsoft's root trust — giving immediate Windows Defender SmartScreen reputation without managing a hardware token or exporting private keys.

- **Cost:** Basic tier starts at **~$9.99/month** (includes 5,000 signatures/month).
- **Eligibility:**
  - **Individual developer path:** Available for individual developers in supported regions using government ID verification (no corporate entity required).
  - **Organization / business path:** Requires a verifiable business identity with at least **3 years** of verifiable business history.
- **Setup overview:**
  1. Create an Artifact Signing Account in the Azure Portal and complete Identity Validation (Individual or Organization).
  2. Create a **Public Trust** Certificate Profile under the account.
  3. Create a Microsoft Entra ID App Registration (service principal) with a client secret and assign it the **Artifact Signing Certificate Profile Signer** role on the signing account.
  4. Add the following repository secrets under **Settings -> Secrets and variables -> Actions**:

| GitHub Secret | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Microsoft Entra ID directory (tenant) ID |
| `AZURE_CLIENT_ID` | Service principal application (client) ID |
| `AZURE_CLIENT_SECRET` | Service principal client secret value |
| `AZURE_SIGNING_ENDPOINT` | Regional endpoint URL (for example `https://eus.codesigning.azure.net`) |
| `AZURE_SIGNING_ACCOUNT` | Artifact Signing account name |
| `AZURE_SIGNING_PROFILE` | Public Trust certificate profile name |

The workflow uses `signtool.exe` from the Windows SDK on `windows-latest` together with `Microsoft.Trusted.Signing.Client` (`Azure.CodeSigning.Dlib.dll`) and timestamps signatures with `http://timestamp.acs.microsoft.com`.

---

## Option B: Traditional EV / OV Authenticode Certificate (PFX)

If you already hold an Authenticode code-signing certificate exported as a password-protected `.pfx` / `.p12` file (for example from a cloud HSM export or existing CA setup), you can configure PFX signing instead.

> **SmartScreen note:** Standard OV certificates build Windows SmartScreen reputation gradually as downloads accumulate; EV certificates typically clear SmartScreen warnings faster, though modern CAs generally issue EV certificates on HSM/cloud-KMS rather than exportable PFX files.

### 1. Base64-encode the `.pfx` file

PowerShell:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("path\to\certificate.pfx")) | Set-Clipboard
```

macOS / Linux:

```sh
base64 -w 0 path/to/certificate.pfx
```

### 2. Add repository secrets

| GitHub Secret | Value |
| --- | --- |
| `WINDOWS_SIGN_PFX_B64` | Single-line Base64-encoded `.pfx` certificate |
| `WINDOWS_SIGN_PFX_PASSWORD` | Password protecting the `.pfx` file |

The workflow writes the decoded `.pfx` to a temporary file inside `$env:RUNNER_TEMP`, signs each executable with `signtool.exe` using SHA-256 and an RFC 3161 timestamp (`http://timestamp.digicert.com`), and deletes the temporary `.pfx` in a `finally` block.

---

## Verifying Signed Binaries

On Windows with the Windows SDK installed (Developer Command Prompt / PowerShell):

```powershell
signtool verify /pa /v Emendly.App.exe
signtool verify /pa /v engine\emendly-engine.exe
signtool verify /pa /v Emendly-v0.1.0-win-x64-setup.exe
```

To verify the release archive checksums against `SHA256SUMS.txt`:

```powershell
Get-FileHash Emendly-v0.1.0-win-x64-setup.exe -Algorithm SHA256
```
