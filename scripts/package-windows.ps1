<#
.SYNOPSIS
  Publishes PkgCleaner for Windows and builds the Inno Setup installer.
.EXAMPLE
  ./scripts/package-windows.ps1               # x64 and arm64
  ./scripts/package-windows.ps1 -Arch x64
.NOTES
  Requires the .NET 10 SDK and Inno Setup 6 (choco install innosetup / winget install JRSoftware.InnoSetup).
  Set $env:SIGN_CERT_THUMBPRINT to Authenticode-sign the exe and installer with signtool.
#>
param(
    [string[]]$Arch = @("x64", "arm64")
)
$ErrorActionPreference = "Stop"

$Root = Resolve-Path "$PSScriptRoot\.."
$Project = "$Root\src\PkgCleaner.Desktop\PkgCleaner.Desktop.csproj"
$Out = "$Root\artifacts"
$Version = ([xml](Get-Content "$Root\Directory.Build.props")).Project.PropertyGroup.Version

$Iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

function Sign($Path) {
    if ($env:SIGN_CERT_THUMBPRINT) {
        signtool sign /sha1 $env:SIGN_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Path
        if ($LASTEXITCODE) { throw "signtool failed for $Path" }
    }
}

New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($a in $Arch) {
    Write-Host "==> Publishing win-$a"
    $Publish = "$Out\windows-$a"
    Remove-Item -Recurse -Force $Publish -ErrorAction SilentlyContinue
    dotnet publish $Project -c Release -r "win-$a" --self-contained -p:DebugType=none -o $Publish -nologo -v quiet
    if ($LASTEXITCODE) { throw "dotnet publish failed" }
    Sign "$Publish\PkgCleaner.exe"

    Write-Host "==> Building installer ($a)"
    & $Iscc /Q "/DAppVersion=$Version" "/DArch=$a" "/DSourceDir=$Publish" "/DOutputDir=$Out" "$Root\installer\windows\PkgCleaner.iss"
    if ($LASTEXITCODE) { throw "ISCC failed" }
    $Setup = "$Out\PkgCleaner-$Version-windows-$a-setup.exe"
    Sign $Setup
    Write-Host "==> $Setup"
}
