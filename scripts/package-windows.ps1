# Publishes a self-contained, unpackaged Windows build (bundles the .NET and Windows App SDK runtimes)
# and zips it. The result is an UNSIGNED development build: Windows SmartScreen may warn on first run.
# Usage: powershell -File scripts/package-windows.ps1 [-Arch x64|arm64]
param([ValidateSet("x64", "arm64")][string]$Arch = "x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "$env:USERPROFILE\.dotnet\dotnet.exe" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$version = ([xml](Get-Content "$root\apps\windows\src\SlothBoss.App\SlothBoss.App.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = "$root\artifacts\windows\SlothBoss-$version-win-$Arch"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

& $dotnet publish "$root\apps\windows\src\SlothBoss.App" -c Release -r "win-$Arch" -p:Platform=$Arch --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item "$root\LICENSE" $out -ErrorAction SilentlyContinue
@"
SlothBoss $version for Windows ($Arch) - unsigned development build

Run SlothBoss.exe. No installation or .NET runtime is needed.
Your workspace is stored in %LOCALAPPDATA%\SlothBoss\workspace.db.
Local AI requires Ollama (https://ollama.com) and a Gemma model, for example:
    ollama pull gemma4:e2b-it-qat
Step-by-step guide: https://github.com/rajeshkhadka200/SlothBoss/blob/main/docs/INSTALL.md
"@ | Set-Content "$out\README.txt" -Encoding utf8

$zip = "$out.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Output "Packaged $zip ($size MB)"
