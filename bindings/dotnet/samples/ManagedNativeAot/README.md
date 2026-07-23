# Managed Turso.Core NativeAOT smoke

This smoke executable directly references `Turso.Core`, opens a file-backed managed database, and verifies create, insert, and select operations without a native provider or runtime asset.

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

Run `make validate-managed-nativeaot` on Windows to publish, execute, and reject native companion assets in the publish output.
