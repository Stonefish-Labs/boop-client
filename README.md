# boop-client

Native non-Apple clients for Boop.

## Layout

```text
boop-client-core/   Rust protocol/state/sync sidecar shared by desktop shells
boop-windows/       Native WinUI 3 Windows client
boop-linux/         Future Linux shell
```

The Windows app starts `boop-client-core` as a sidecar process and talks to it
over NDJSON JSON-RPC. The sidecar owns server profiles, credential storage,
HTTP API calls, websocket sync, and notification candidate events.

## Windows Build

Build the sidecar:

```powershell
cargo build --manifest-path .\boop-client-core\Cargo.toml --release
```

Build the WinUI app:

```powershell
dotnet restore .\boop-windows\src\Boop.Windows\Boop.Windows.csproj
dotnet build .\boop-windows\src\Boop.Windows\Boop.Windows.csproj -c Debug -p:Platform=x64
```

If the app cannot find the sidecar, set:

```powershell
$env:BOOP_CLIENT_CORE_PATH = "C:\path\to\boop-client\boop-client-core\target\release\boop-client-core.exe"
```

## Notes

Windows notifications are local-first in v1: the app must be running or
minimized to tray. WNS/Azure push and Linux UI work are deferred.

