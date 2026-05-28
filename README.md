# Boop Client

Native clients and local client runtime for Boop.

## Projects

- `boop-client-core`: Rust sidecar that owns enrollment, persistence, API calls, sync, and local state.
- `boop-windows`: WinUI 3 Windows client that shells the Rust sidecar.

## Prerequisites

- .NET 10 SDK
- Rust stable MSVC toolchain
- Visual Studio or Build Tools with .NET desktop development, Desktop development with C++, and Windows SDK 10.0.19041 or newer
- Windows App SDK runtime from Microsoft: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads

## Build

From this repository root:

```powershell
cargo build --manifest-path .\boop-client-core\Cargo.toml --release
dotnet restore .\boop-windows\src\Boop.Windows\Boop.Windows.csproj
dotnet build .\boop-windows\src\Boop.Windows\Boop.Windows.csproj -c Debug -p:Platform=x64
```

The Windows project copies `boop-client-core\target\release\boop-client-core.exe` next to the app when the sidecar has been built. For development, you can also point the app at a sidecar explicitly:

```powershell
$env:BOOP_CLIENT_CORE_PATH = "C:\path\to\boop-client\boop-client-core\target\release\boop-client-core.exe"
```

Run the app with:

```powershell
dotnet run --project .\boop-windows\src\Boop.Windows\Boop.Windows.csproj -c Debug -p:Platform=x64
```

## Test

```powershell
cargo test --manifest-path .\boop-client-core\Cargo.toml
dotnet build .\boop-windows\src\Boop.Windows\Boop.Windows.csproj -c Release -p:Platform=x64
```

## Notes

The Windows client currently uses local Windows App SDK notifications. The app must be running or minimized to tray to receive local notifications; WNS push is not wired up yet.
