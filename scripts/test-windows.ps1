# Runs the Windows test suite:
#   1. Core unit + contract-conformance tests (dotnet test)
#   2. Editor self-test inside a live WinUI RichEditBox (needs an interactive desktop session)
#   3. Optional live tests against a local Ollama model: -LiveModel gemma4:e2b-it-qat
# Usage: powershell -File scripts/test-windows.ps1 [-LiveModel <name>] [-SkipEditor]
param([string]$LiveModel = "", [switch]$SkipEditor)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "$env:USERPROFILE\.dotnet\dotnet.exe"; $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Write-Output "== Core tests =="
& $dotnet test "$root\apps\windows\tests\SlothBoss.Core.Tests" --logger "console;verbosity=minimal"
if ($LASTEXITCODE -ne 0) { throw "Core tests failed" }

if (-not $SkipEditor) {
    Write-Output "== Editor self-test (live RichEditBox) =="
    & powershell -NoProfile -File "$root\scripts\dev-run.ps1" -SelfTest
    if ($LASTEXITCODE -ne 0) { throw "Editor self-test failed" }
}

if ($LiveModel) {
    Write-Output "== Live Ollama tests ($LiveModel) =="
    $env:SLOTHBOSS_OLLAMA_MODEL = $LiveModel
    & $dotnet test "$root\apps\windows\tests\SlothBoss.Core.Tests" --no-build --filter "FullyQualifiedName~.Live" --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "Live Ollama tests failed" }
}
Write-Output "All requested Windows tests passed."
