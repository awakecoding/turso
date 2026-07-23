# Managed Turso.Core NativeAOT smoke

This smoke executable directly references `Turso.Core`, opens a file-backed managed database, and verifies create, insert, and select operations without a native provider or runtime asset.

```bash
make validate-managed-nativeaot
```

The gate chooses the current host RID (or accepts `MANAGED_NATIVEAOT_RID=<rid>`),
publishes and executes the sample, and verifies that its restore and publish
closures contain no native Turso package, P/Invoke or Rust build dependency, or
runtime asset; the executable's symbols and XML documentation are the only
additional publish files permitted.
