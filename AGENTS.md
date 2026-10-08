# Repository Guidelines

## Project Structure

Assister Windows is a C# WPF desktop client targeting .NET 10. `Assister.Windows.App/` contains the application, XAML views, and services. Keep UI in WPF, protocol and transport code under `Services/` or focused sibling folders, and reusable state/models outside window code. `docs/progress.md` tracks feature parity with the Android client. `WINDOWS-PORT-HANDOFF.md` summarizes the Android behavior and Windows-specific translation guidance.

## Build and Run

Use the .NET 10 SDK on Windows with the Windows Desktop runtime:

```powershell
dotnet build .\Assister.Windows.sln
dotnet run --project .\Assister.Windows.App\Assister.Windows.App.csproj
dotnet publish .\Assister.Windows.App\Assister.Windows.App.csproj -c Release
```

The app uses the Assister rich-client v1 API. Read the current [canonical protocol](https://github.com/GaryJS3/Assister/blob/main/docs/rich-client-protocol.md) before changing wire behavior; Android examples and this repository's handoff are context, not protocol authority.

## C# and XAML Style

Prefer C#. Use four spaces, nullable reference types, `PascalCase` for types and public members, and `camelCase` for locals and parameters. Keep async I/O off the UI thread, pass cancellation tokens, and keep window code focused on presentation. Use descriptive XAML names and extract reusable controls when a view gains substantial behavior. No formatter or analyzer is configured yet.

## Verification

There is no test project yet. Build after each code change with `dotnet build .\Assister.Windows.sln`. Add focused regression tests when introducing reducers, persistence, reconnect/replay, or other independently testable behavior; record commands and outcomes in `docs/progress.md`. Do not describe a feature as complete based on scaffolding alone.

The primary physical UI target is the Windows tablet `Tab-Dev` at `10.0.0.197`, with Visual Studio 2026 Remote Debugger available. Use it for touch, scaling, orientation, microphone, speaker, and lifecycle acceptance as those features are implemented; record which checks actually ran.

## Commits and Pull Requests

The remote repository started empty, so no established commit convention exists. Use short imperative subjects (for example, `Add reconnecting event client`). PRs should explain user-visible behavior, link relevant issues, list build/device validation and limitations, and include screenshots for WPF layout changes.

## Security and Parity Tracking

Never commit bearer tokens, credentials, signing material, or user data. The app stores its token in Windows Credential Manager; keep that boundary and require HTTPS except for the explicitly permitted development LAN endpoint. Update `docs/progress.md` when implementation or verification changes, and consult the Android repository for behavior while treating each Windows feature as incomplete until implemented and validated here.

The local tablet deployment credential is stored in the ignored root `tab-dev-credentials.xml` as a Windows DPAPI-protected PowerShell credential. It can be decrypted only by the same Windows user on this workstation; do not replace it with plaintext or copy it to source control.
