# Installs the latest TermNest .msix from dist/.
# - Trusts the self-signed signing cert in LocalMachine\TrustedPeople once
#   (admin-elevated on first run only).
# - Calls Add-AppxPackage to install/upgrade the package for the current user.
#
# Re-run anytime: trust step is no-op once cert is in TrustedPeople, install
# is upgrade-in-place if the version is the same or higher.

$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $root 'dist'

$msix = Get-ChildItem -Path $dist -Recurse -Filter '*.msix' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch 'bundle' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msix) { throw "No .msix found under $dist. Run scripts\build-msix.ps1 first." }

Write-Host "==> trusting signing cert (admin elevation may be needed)" -ForegroundColor Cyan
$publisher = 'CN=ShroomlifeDev'
$cert = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) { throw "Signing cert not found in CurrentUser\\My; run scripts\\build-msix.ps1 first." }

# Already in TrustedPeople? Skip.
$alreadyTrusted = Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
    Select-Object -First 1

if (-not $alreadyTrusted) {
    # Export to temp + Import-Certificate to LocalMachine\TrustedPeople. This step
    # MUST run elevated; if not, re-spawn this script with -Verb RunAs.
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
        Write-Host "    (relaunching elevated to install cert)" -ForegroundColor DarkGray
        Start-Process -FilePath powershell.exe `
            -Verb RunAs `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath) `
            -Wait
        return
    }

    $tmp = Join-Path $env:TEMP "TermNest-signing-$($cert.Thumbprint).cer"
    Export-Certificate -Cert $cert -FilePath $tmp -Type CERT | Out-Null
    Import-Certificate -FilePath $tmp -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Remove-Item $tmp -Force
    Write-Host "    cert trusted (thumbprint $($cert.Thumbprint))" -ForegroundColor DarkGray
} else {
    Write-Host "    cert already trusted (thumbprint $($cert.Thumbprint))" -ForegroundColor DarkGray
}

Write-Host "==> Add-AppxPackage $($msix.Name)" -ForegroundColor Cyan
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "Installed:" -ForegroundColor Green
Get-AppxPackage -Name *TermNest* | Format-List Name, Version, InstallLocation
Write-Host "Find it as 'TermNest' in the Start menu." -ForegroundColor Yellow
