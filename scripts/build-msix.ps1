# Builds TermNest as a sideloadable, self-signed MSIX.
# - On first run, generates a code-signing cert in CurrentUser\My matching
#   the Publisher in Package.appxmanifest (CN=ShroomlifeDev).
# - Passes the cert thumbprint to MSBuild's APPX signing pipeline.
# - Triggers a Release / x64 build with GenerateAppxPackageOnBuild=true.
# - Final .msix lands in dist/ at the repo root.

$ErrorActionPreference = 'Stop'

$root      = Resolve-Path "$PSScriptRoot\.."
$appProj   = Join-Path $root 'src\TermNest.App\TermNest.App.csproj'
$publisher = 'CN=ShroomlifeDev'   # MUST match Package.appxmanifest <Identity Publisher="...">
$certPath  = Join-Path $root '.cert-thumbprint'

# Find or create the signing certificate in CurrentUser\My.
$cert = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "==> generating self-signed sideload cert ($publisher)" -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $publisher `
        -KeyUsage DigitalSignature `
        -FriendlyName 'TermNest sideload key' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "    thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGray
} else {
    Write-Host "==> reusing existing cert (thumbprint $($cert.Thumbprint))" -ForegroundColor DarkGray
}

# Cache the thumbprint so users can run scripts/install-msix.ps1 without
# regenerating logic.
Set-Content -Path $certPath -Value $cert.Thumbprint -NoNewline

Write-Host "==> dotnet build (Release / x64 / msix)" -ForegroundColor Cyan
dotnet build $appProj `
    -c Release `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
    -p:UapAppxPackageBuildMode=SideloadOnly
if ($LASTEXITCODE) { throw "dotnet build failed ($LASTEXITCODE)" }

# Find the produced .msix.
$dist = Join-Path $root 'dist'
$msix = Get-ChildItem -Path $dist -Recurse -Filter '*.msix' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch 'bundle' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msix) { throw "No .msix was produced under $dist" }

Write-Host ""
Write-Host "MSIX built successfully:" -ForegroundColor Green
Get-Item $msix.FullName | Format-List FullName, Length, LastWriteTime

Write-Host "Install with: scripts\install-msix.ps1   (run once as administrator to trust the cert)" -ForegroundColor Yellow
