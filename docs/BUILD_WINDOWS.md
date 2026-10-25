# Building the Windows client

## Prerequisites
- Windows 10 1809 (build 17763) or later. Developed and tested on Windows 11 Pro build 26200, x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio is **not** required — the WinUI 3
  XAML compiler ships in the Windows App SDK NuGet package and runs under `dotnet build`.
- Optional: Ollama + a Gemma model for AI features ([LOCAL_AI_SETUP.md](LOCAL_AI_SETUP.md)).

## Build and run (development)
```powershell
dotnet test  apps/windows/tests/SlothBoss.Core.Tests
dotnet build apps/windows/src/SlothBoss.App -c Debug -p:Platform=x64 -r win-x64
apps/windows/src/SlothBoss.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/SlothBoss.exe
```
Or use the helper, which builds, stops on errors and opens a throwaway workspace in `%TEMP%`:
```powershell
powershell -File scripts/dev-run.ps1 -Fresh
```
If your SDK is installed per-user (not in `C:\Program Files\dotnet`), set
`DOTNET_ROOT=%USERPROFILE%\.dotnet` before launching a Debug build; `dev-run.ps1` does this.

### Command-line options
| Option | Purpose |
|---|---|
| `--workspace <file.db>` | open a specific workspace for this session (demos, testing) |
| `--practice-folder <parent dir>` | create a synthetic messy folder under that directory and approve it for agent jobs |
| `--selftest-editor <contracts/fixtures> <report.txt>` | run the live editor self-test and exit |

## Package (self-contained, unsigned)
```powershell
powershell -File scripts/package-windows.ps1 -Arch x64     # or -Arch arm64
```
Produces `artifacts/windows/SlothBoss-<version>-win-x64.zip` (~85 MB). It bundles the .NET runtime and the
Windows App SDK runtime: unzip and run `SlothBoss.exe`, no installation. Verified on the development
machine to start without any .NET runtime on `PATH`.

The project is an **unpackaged** WinUI app (`WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`). `EnableMsixTooling=true` is required so `dotnet publish` includes the
compiled XAML resources (`SlothBoss.pri`); without it the published app crashes at startup.

### Signing and installers — status
- The zip is an **unsigned development build**. Windows SmartScreen may warn on first launch
  ("More info → Run anyway").
- No MSIX or installer is produced yet. A signed MSIX needs a code-signing certificate, which this
  project does not have. ARM64 packaging is configured but was not built or tested.

## CI
`.github/workflows/windows.yml` runs the core tests, builds the app, attempts the editor self-test
(non-blocking on hosted runners) and uploads the packaged zip.
