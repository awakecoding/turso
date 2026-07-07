# Turso NativeAOT sample

This sample publishes a NativeAOT executable that statically links `turso_sdk_kit` from the `Turso.Data.Sqlite` package:

```bash
dotnet publish -c Release -r win-x64
```

Set `<TursoUseStaticNativeLibrary>true</TursoUseStaticNativeLibrary>` with `<PublishAot>true</PublishAot>` to enable the NuGet package's static native assets. Supported runtime identifiers are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

When using an unreleased local package, add the package output folder as a restore source, for example:

```bash
dotnet publish -c Release -r win-x64 -p:RestoreAdditionalProjectSources=..\..\src\Turso.Data.Sqlite\bin\Release
```
