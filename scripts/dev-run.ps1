# Builds the Windows app (Debug, x64) and launches it against a throwaway workspace.
# Stops on build errors. Usage: powershell -File scripts/dev-run.ps1 [-Fresh] [-SelfTest]
param([switch]$Fresh, [switch]$SelfTest, [string]$Workspace = "$env:TEMP\slothboss-demo\workspace.db")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Get-Process SlothBoss -ErrorAction SilentlyContinue | Stop-Process -Force
$out = & $dotnet build "$root\apps\windows\src\SlothBoss.App" -c Debug -p:Platform=x64 -r win-x64 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Select-String " error " | ForEach-Object { $_.Line -replace '\[C:.*\]$', '' } | Select-Object -Unique
    throw "Build failed"
}
$exe = "$root\apps\windows\src\SlothBoss.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\SlothBoss.exe"

if ($SelfTest) {
    $report = "$env:TEMP\slothboss-selftest.txt"
    Remove-Item $report -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList "--selftest-editor", "`"$root\contracts\fixtures`"", "`"$report`"" -PassThru
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "Editor self-test timed out" }
    Get-Content $report
    if (-not (Select-String -Path $report -Pattern "ALL PASSED" -Quiet)) { throw "Editor self-test failed" }
    return
}

if ($Fresh) { Remove-Item -Recurse -Force (Split-Path $Workspace) -ErrorAction SilentlyContinue }
$p = Start-Process $exe -ArgumentList "--workspace", "`"$Workspace`"" -PassThru
Write-Output "started pid $($p.Id) with workspace $Workspace"
