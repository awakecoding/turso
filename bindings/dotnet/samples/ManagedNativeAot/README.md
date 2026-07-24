# Managed Turso package NativeAOT smoke

This smoke executable restores the packed `Turso.Data.Sqlite` package and uses
its bundled `Turso.Core` managed engine to open a file-backed database and verify
create, insert, and select operations without a native provider or runtime asset.

```bash
make validate-managed-nativeaot
```

The gate chooses the current host RID (or accepts `MANAGED_NATIVEAOT_RID=<rid>`),
packs and restores the primary managed package, then publishes and executes the
sample. It rejects native Turso package, Rust build-tool, and runtime-asset
edges; the executable's symbols and XML documentation are the only additional
publish files permitted.
