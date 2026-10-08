# Assister Windows

A self-contained C# WPF client for the Assister rich-client API. The Windows app is being built toward the feature set of [Assister Android](https://github.com/GaryJS3/Assister-Android), with Windows-specific credential, audio, device, and package integrations.

## Development

Requirements: Windows, .NET 10 SDK, and Windows Desktop runtime.

```powershell
dotnet build .\Assister.Windows.sln
dotnet run --project .\Assister.Windows.App\Assister.Windows.App.csproj
```

Open **Settings** in the app to enter a rich-client server origin and bearer token. Tokens are kept in Windows Credential Manager. For local development, the app can also read the ignored repository-root `assister-dev-token.txt`; a token saved in Credential Manager takes precedence. HTTPS is required by default; plain HTTP is allowed for localhost and private IPv4 development-LAN hosts (for example, `http://10.44.0.33:8081`). The token must match a `RichClients:Clients:<clientId>:Token` entry on the server.

Read [AGENTS.md](AGENTS.md) for contribution guidance and [docs/progress.md](docs/progress.md) for implemented features, parity work, and verification status. Before changing API behavior, consult the [canonical rich-client protocol](https://github.com/GaryJS3/Assister/blob/main/docs/rich-client-protocol.md).

For local development, keep the rich-client token in the ignored root `assister-dev-token.txt`. The Windows publisher and server release route are not implemented yet.

## Automatic Windows updates

Published x64 releases install under `%LOCALAPPDATA%\Assister\App` so the app can update without UAC. The published EXE bootstraps that user installation when first launched. Keep launching that installed copy after initial setup; an old seed EXE should not be reused.

The app checks the configured server at startup and every 15 minutes using `/api/updates/assister/windows-x64/check`. Public release requests never send chat or upload credentials. Downloads must remain on the configured origin and match release identity, version, size, and SHA-256. Updates wait for active work, draft text, and settings windows to clear, then automatically exit, replace the installed EXE with a helper, and restart. The previous binary is retained as `Assister.Windows.App.exe.previous`; replacement/start failure restores it. A later application crash does not trigger automatic rollback.

Publish a higher numeric version with:

```powershell
.\Publish-Windows.ps1 -Version 2026.10.7.2
```

The publisher reads the ignored `assister-dev-token.txt`, builds a self-contained single-file executable, uploads it to the existing server release API, and verifies the public download. Release versions are immutable. Update diagnostics are in `%LOCALAPPDATA%\Assister\update.log` (rotated at 1 MiB). Production release origins must use HTTPS; private development HTTP uses the existing LAN exception. SHA-256 verifies consistency with server metadata; packages are currently unsigned and depend on the trusted server/transport.
