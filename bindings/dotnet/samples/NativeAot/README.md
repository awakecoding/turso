# Turso NativeAOT sample

This sample publishes a NativeAOT executable that statically links `turso_sdk_kit` from the RID-specific `Turso.Data.Sqlite.NativeAot.*` package and selects `Local Provider=Native`:

```bash
dotnet publish -c Release -r win-x64
```

Set `<TursoUseStaticNativeLibrary>true</TursoUseStaticNativeLibrary>` with `<PublishAot>true</PublishAot>` and reference the matching `Turso.Data.Sqlite.NativeAot.<rid>` package to enable static native assets and its carried native-provider assembly. The release-gated matrix is the generic `net8.0`, `net9.0`, and `net10.0` TFMs on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Mobile and OS-qualified TFMs are intentionally rejected; use the dynamic Android or iOS package assets for workload-specific app builds.

When using an unreleased local package, add the package output folder as a restore source, for example:

```bash
dotnet publish -c Release -r win-x64 -p:RestoreAdditionalProjectSources=..\..\artifacts\nuget-packages
```
