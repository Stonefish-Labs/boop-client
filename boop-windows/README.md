# Boop Windows

Native WinUI 3 client for Boop. The UI shell is C# and the protocol/state engine is
the Rust `boop-client-core` sidecar.

## Build

Build the sidecar first from the repository root:

```powershell
cargo build --manifest-path ..\boop-client-core\Cargo.toml --release
```

Then build the packaged Windows app on Windows:

```powershell
dotnet restore .\src\Boop.Windows\Boop.Windows.csproj
dotnet build .\src\Boop.Windows\Boop.Windows.csproj -c Release
```

For local development, set `BOOP_CLIENT_CORE_PATH` to the sidecar executable if it
is not copied next to `Boop.Windows.exe`.

## Scope

Windows v1 uses local app notifications while the app is running or minimized to
the tray. WNS push and Linux UI shells are intentionally deferred.

