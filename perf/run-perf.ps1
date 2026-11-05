<#
.SYNOPSIS
  Builds Nib (Release, Native AOT) and runs the performance suite. Exit code 0 = every target met.

.EXAMPLE
  .\perf\run-perf.ps1 -Label "Surface Go 3 (battery saver)"
  .\perf\run-perf.ps1 -Platform ARM64 -Trace     # also records an ETW trace for WPA
  .\perf\run-perf.ps1 -Cpu lunarlake -Label "Yoga 7 2-in-1 14ILL10"   # CPU-tuned build for Core Ultra 200V
#>
param(
    [ValidateSet("x64", "ARM64")] [string] $Platform = "x64",
    [string] $Label = $env:COMPUTERNAME,
    [ValidateSet("", "lunarlake", "native")] [string] $Cpu = "",
    [switch] $Trace
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }

Write-Host "Unit tests and CPU budgets..." -ForegroundColor Cyan
dotnet test "$root\tests\Nib.Core.Tests" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing Nib ($rid, Native AOT)..." -ForegroundColor Cyan
$cpuArgs = if ($Cpu) { @("-p:NibCpu=$Cpu") } else { @() }
dotnet publish "$root\src\Nib.App" -c Release -r $rid -p:Platform=$Platform @cpuArgs -o "$root\artifacts\app-$rid"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build "$root\tests\Nib.PerfRunner" -c Release -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Trace) { wpr -start "$PSScriptRoot\Nib.wprp!Nib" -filemode }
try {
    $runner = Get-ChildItem "$root\tests\Nib.PerfRunner\bin" -Recurse -Filter Nib.PerfRunner.exe | Where-Object { $_.FullName -match '\\Release\\' } | Select-Object -First 1
    & $runner.FullName --app "$root\artifacts\app-$rid\Nib.exe" --targets "$PSScriptRoot\perf-targets.json" --out "$root\artifacts\perf-$rid" --label $Label
    $code = $LASTEXITCODE
}
finally {
    if ($Trace) {
        wpr -stop "$root\artifacts\perf-$rid\nib.etl"
        Write-Host "Trace: $root\artifacts\perf-$rid\nib.etl (open in Windows Performance Analyzer)"
    }
}
exit $code
