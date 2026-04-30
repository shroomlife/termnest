# Exports the existing TermNest sideload signing cert (CN=ShroomlifeDev) as a
# password-protected .pfx and prints a base64 blob suitable for the
# SIGNING_CERTIFICATE_BASE64 GitHub repo secret. Run once after creating
# the cert via scripts/build-msix.ps1.
#
# Usage:
#   scripts\export-signing-cert-for-ci.ps1                      # prompts for password
#   scripts\export-signing-cert-for-ci.ps1 -Password "secret"   # non-interactive
#
# The .pfx is written to dist\signing-cert.pfx. NEVER commit it — dist\ is
# already .gitignored, but treat the file as a secret.
#
# After running:
#   gh secret set SIGNING_CERTIFICATE_BASE64    --repo shroomlife/termnest --body "$(Get-Content dist\signing-cert.b64 -Raw)"
#   gh secret set SIGNING_CERTIFICATE_PASSWORD  --repo shroomlife/termnest --body "<your password>"

[CmdletBinding()]
param(
    [string] $Password,
    [string] $Publisher = 'CN=ShroomlifeDev'
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$cert = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    throw "No code-signing cert with subject '$Publisher' found in Cert:\CurrentUser\My. Run scripts\build-msix.ps1 first to generate it."
}

if (-not $Password) {
    $secure = Read-Host -AsSecureString -Prompt 'PFX export password (will become SIGNING_CERTIFICATE_PASSWORD)'
} else {
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
}

$pfxPath = Join-Path $dist 'signing-cert.pfx'
$b64Path = Join-Path $dist 'signing-cert.b64'

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
$bytes = [IO.File]::ReadAllBytes($pfxPath)
$b64   = [Convert]::ToBase64String($bytes)
Set-Content -Path $b64Path -Value $b64 -NoNewline

Write-Host ""
Write-Host "Exported signing cert:" -ForegroundColor Green
Write-Host "  thumbprint: $($cert.Thumbprint)"
Write-Host "  pfx:        $pfxPath"
Write-Host "  base64:     $b64Path  ($([Math]::Round($b64.Length / 1KB, 1)) KB)"
Write-Host ""
Write-Host "Set the GitHub Actions secrets:" -ForegroundColor Yellow
Write-Host "  gh secret set SIGNING_CERTIFICATE_BASE64    --repo shroomlife/termnest --body `"`$(Get-Content '$b64Path' -Raw)`""
Write-Host "  gh secret set SIGNING_CERTIFICATE_PASSWORD  --repo shroomlife/termnest"
Write-Host ""
Write-Host "Once both secrets are set, push a tag (e.g. 'git tag v1.0.0.2 && git push origin v1.0.0.2') to trigger the Release workflow."
