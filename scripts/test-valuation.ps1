# Builds OPTIC and runs the test suite on Windows.
#
#   powershell -ExecutionPolicy Bypass -File scripts\test-valuation.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\test-valuation.ps1 -Filter OsmosisMathTests
#
# Output goes to a separate artifacts folder, so it works while OPTIC is running (dotnet run locks OPTIC\bin).

param(
    [string]$Filter = "",
    [string]$ArtifactsPath = (Join-Path $env:TEMP "optic-test-artifacts")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Building OPTIC and OPTIC.Tests into $ArtifactsPath"
dotnet build OPTIC.Tests\OPTIC.Tests.csproj --artifacts-path $ArtifactsPath -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$testArgs = @("test", "OPTIC.Tests\OPTIC.Tests.csproj", "--artifacts-path", $ArtifactsPath, "--no-build", "-nologo", "--logger", "console;verbosity=normal")
if ($Filter) { $testArgs += @("--filter", $Filter) }
& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
