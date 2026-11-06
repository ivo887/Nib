<#
.SYNOPSIS
  Publishes Nib (Release, Native AOT) and packs it into a per-user MSI: artifacts\Nib-<version>-<arch>.msi

.EXAMPLE
  .\installer\build-installer.ps1                    # generic x64 build, runs on any x64 PC
  .\installer\build-installer.ps1 -Cpu lunarlake     # tuned for Core Ultra 200V only
  .\installer\build-installer.ps1 -Platform ARM64
#>
param(
    [ValidateSet("x64", "ARM64")] [string] $Platform = "x64",
    [ValidateSet("", "lunarlake", "native")] [string] $Cpu = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }

# The AOT linker needs vswhere on PATH.
$vs = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vs) -and ($env:PATH -notlike "*$vs*")) { $env:PATH += ";$vs" }

# Version comes from Directory.Build.props (MSI wants a.b.c.d).
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$msiVersion = (($version -split '\.') + @('0', '0', '0', '0') | Select-Object -First 4) -join '.'

$stage = "$root\artifacts\installer-$rid"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "Publishing Nib $version ($rid, Native AOT)..." -ForegroundColor Cyan
$cpuArgs = if ($Cpu) { @("-p:NibCpu=$Cpu") } else { @() }
dotnet publish "$root\src\Nib.App" -c Release -r $rid -p:Platform=$Platform @cpuArgs -o $stage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$suffix = if ($Cpu) { "-$Cpu" } else { "" }
$msi = "$root\artifacts\Nib-$version-$Platform$suffix.msi"
Write-Host "Building $msi ..." -ForegroundColor Cyan
Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet wix build "$PSScriptRoot\Nib.wxs" -arch $Platform.ToLower() `
        -d Version=$msiVersion -d SourceDir=$stage -d IconFile="$root\src\Nib.App\Assets\Nib.ico" -o $msi
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally { Pop-Location }

Write-Host ("Done: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
