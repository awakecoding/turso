using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Turso.Tests;

[NonParallelizable]
public class TursoDataSqlitePackageArtifactReleaseTests
{
    private const string RawPackageTargetFrameworks = "net8.0;net9.0;net10.0";
    private const string ManagedPackageDescription =
        "Managed ADO.NET package for Turso: TursoConnection supports managed local and remote Hrana databases, while Turso.Data.Sqlite is a local-only Microsoft.Data.Sqlite-compatible facade. Native local and embedded replica modes use optional companion packages.";
    private const string NativePackageDescription =
        "Optional dynamic native local-provider companion for Turso.Data.Sqlite. Select Local Provider=Native; Turso.Raw supplies the desktop and mobile runtime assets.";
    private const string SyncPackageDescription =
        "Optional native embedded-replica and explicit Sync companion for TursoConnection, with desktop and mobile runtime assets.";
    private const string RawPackageDescription =
        "Dynamic native interop and runtime assets for the optional Turso.Data.Sqlite.Native local provider on Windows, Linux, macOS, Android, and iOS.";
    private const string EfPackageDescription =
        "Local-only Entity Framework Core 9.x provider for Turso.Data.Sqlite, using EF Core SQLite translation with managed or native local execution.";
    private const string NativeAotPackageDescription =
        "RID-specific static native library assets for net8.0, net9.0, and net10.0 NativeAOT desktop publishing with Turso.Data.Sqlite.";

    [Test]
    public void PackageContainsManagedDependenciesWithoutRawAndLoadsManagedConnection()
    {
        var packageDirectory = CreatePackageDirectory("turso-package-validation");

        try
        {
            var projectPath = FindProjectPath();
            var packageVersion = $"0.0.0-package-validation-{Guid.NewGuid():N}";
            var efProjectPath = Path.Combine(
                Path.GetDirectoryName(projectPath)!,
                "..",
                "Turso.EntityFrameworkCore.Sqlite",
                "Turso.EntityFrameworkCore.Sqlite.csproj");
            BuildForPackage(projectPath, "net9.0");
            BuildForPackage(efProjectPath, "net9.0");
            Pack(projectPath, packageDirectory, packageVersion);
            Pack(efProjectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(packageDirectory, $"Turso.Data.Sqlite.{packageVersion}.nupkg");
            File.Exists(packagePath).Should().BeTrue();
            AssertPackageMetadata(packagePath, ManagedPackageDescription);
            AssertPackageMetadata(
                Path.Combine(
                    packageDirectory,
                    $"Turso.EntityFrameworkCore.Sqlite.{packageVersion}.nupkg"),
                EfPackageDescription);

            var extractionDirectory = Path.Combine(packageDirectory, "extracted");
            ZipFile.ExtractToDirectory(packagePath, extractionDirectory);
            var libraryDirectory = Path.Combine(extractionDirectory, "lib", "net9.0");
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                archive.Entries
                    .Should()
                    .NotContain(entry =>
                        entry.FullName.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("build/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("buildTransitive/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("native/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("turso_sdk_kit", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("Turso.Raw", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("Turso.Data.Native", StringComparison.OrdinalIgnoreCase),
                        "the managed package must not carry native implementation assets or assemblies");

                var nuspecEntry = archive.Entries.Single(entry =>
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                using var nuspecStream = nuspecEntry.Open();
                var nativeDependencies = XDocument.Load(nuspecStream)
                    .Descendants()
                    .Where(element => element.Name.LocalName == "dependency")
                    .Select(element => element.Attribute("id")?.Value)
                    .Where(id => id is not null)
                    .ToArray();
                nativeDependencies.Should().NotContain(
                    dependency => IsNativeCompanionPackage($"{dependency}/"),
                    "the managed package must not restore an optional native companion");
            }

            foreach (var file in new[]
                     {
                         "Turso.Data.Sqlite.dll",
                         "Turso.Data.dll",
                         "Turso.Data.xml",
                         "Turso.Core.dll",
                         "Turso.Core.xml",
                     })
            {
                File.Exists(Path.Combine(libraryDirectory, file)).Should().BeTrue($"{file} must be present for managed package consumers");
            }

            EnsureUnloaded(LoadManagedConnection(libraryDirectory));
            RunManagedPackageConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    [Test]
    public void ManagedPackageExposesReplicaOptionsWithoutSyncCompanion()
    {
        var packageDirectory = CreatePackageDirectory("turso-managed-replica-options-validation");

        try
        {
            var packageVersion = $"0.0.0-managed-replica-options-{Guid.NewGuid():N}";
            var projectPath = FindProjectPath();
            BuildForPackage(projectPath, "net9.0");
            Pack(projectPath, packageDirectory, packageVersion);
            RunManagedReplicaOptionsConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    [Test]
    public void NativeCompanionPackageRoutesExplicitNativeConnections()
    {
        NativeCompanionAvailability.RequireSdkKit();
        var packageDirectory = CreatePackageDirectory("turso-native-package-validation");

        try
        {
            var packageVersion = $"0.0.0-native-package-validation-{Guid.NewGuid():N}";
            var projectDirectory = Path.GetDirectoryName(FindProjectPath())!;
            var sqliteProjectPath = Path.Combine(projectDirectory, "Turso.Data.Sqlite.csproj");
            BuildForPackage(sqliteProjectPath, "net9.0");
            Pack(sqliteProjectPath, packageDirectory, packageVersion);
            PackRawPackage(
                Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj"),
                packageDirectory,
                packageVersion);
            var nativeProjectPath = Path.Combine(projectDirectory, "..", "Turso.Data.Native", "Turso.Data.Native.csproj");
            Restore(nativeProjectPath);
            BuildNativeCompanion(nativeProjectPath);
            Pack(nativeProjectPath, packageDirectory, packageVersion);

            AssertPackageMetadata(
                Path.Combine(packageDirectory, $"Turso.Data.Sqlite.Native.{packageVersion}.nupkg"),
                NativePackageDescription);
            RunNativePackageConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    [Test]
    public void SyncCompanionPackageDeclaresFacadeAndOpensDeferredReplica()
    {
        NativeCompanionAvailability.RequireSyncSdkKit();
        var packageDirectory = CreatePackageDirectory("turso-sync-package-validation");

        try
        {
            var packageVersion = $"0.0.0-sync-package-validation-{Guid.NewGuid():N}";
            var sqliteProjectPath = FindProjectPath();
            var projectDirectory = Path.GetDirectoryName(sqliteProjectPath)!;
            var syncProjectPath = Path.Combine(
                projectDirectory,
                "..",
                "Turso.Data.Sync",
                "Turso.Data.Sync.csproj");

            BuildForPackage(sqliteProjectPath, "net9.0");
            Pack(sqliteProjectPath, packageDirectory, packageVersion);
            Restore(syncProjectPath);
            BuildForPackage(syncProjectPath, "net9.0");
            Pack(syncProjectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(
                packageDirectory,
                $"Turso.Data.Sqlite.Sync.{packageVersion}.nupkg");
            AssertPackageMetadata(packagePath, SyncPackageDescription);
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var nuspecEntry = archive.Entries.Single(entry =>
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                using var nuspecStream = nuspecEntry.Open();
                var facadeDependencyVersions = XDocument.Load(nuspecStream)
                    .Descendants()
                    .Where(element => element.Name.LocalName == "dependency")
                    .Where(element =>
                        string.Equals(
                            element.Attribute("id")!.Value,
                            "Turso.Data.Sqlite",
                            StringComparison.Ordinal))
                    .Select(element => element.Attribute("version")!.Value)
                    .ToArray();

                facadeDependencyVersions.Should().NotBeEmpty()
                    .And.OnlyContain(version => version == packageVersion);
            }

            RunSyncPackageConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    [Test]
    public void RawPackageContainsManagedClosureForEveryTargetFramework()
    {
        var packageDirectory = CreatePackageDirectory("turso-raw-package-validation");

        try
        {
            var packageVersion = $"0.0.0-raw-package-validation-{Guid.NewGuid():N}";
            var projectDirectory = Path.GetDirectoryName(FindProjectPath())!;
            var rawProjectPath = Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj");
            PackRawPackage(rawProjectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(packageDirectory, $"Turso.Raw.{packageVersion}.nupkg");
            File.Exists(packagePath).Should().BeTrue();
            AssertPackageMetadata(packagePath, RawPackageDescription);

            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var targetFramework in RawPackageTargetFrameworks.Split(';'))
            {
                foreach (var assembly in new[] { "Turso.Raw.dll", "Turso.Core.dll", "Turso.Data.dll" })
                {
                    archive.Entries.Should().Contain(
                        entry => string.Equals(
                            entry.FullName,
                            $"lib/{targetFramework}/{assembly}",
                            StringComparison.OrdinalIgnoreCase),
                        $"{assembly} must be present for Raw package consumers targeting {targetFramework}");
                }
            }

            var nuspecEntry = archive.Entries.Single(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);
            var dependencyGroups = nuspec
                .Descendants()
                .Where(element => element.Name.LocalName == "group")
                .ToArray();

            dependencyGroups
                .Select(group => group.Attribute("targetFramework")!.Value)
                .Should()
                .BeEquivalentTo(RawPackageTargetFrameworks.Split(';'));
            dependencyGroups.Should().OnlyContain(group =>
                !group.Elements().Any(element => element.Name.LocalName == "dependency"));
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    [Test]
    public void NativeAotStaticPackageDeclaresManagedFacadeAndRestoresClosure()
    {
        var packageDirectory = CreatePackageDirectory("turso-nativeaot-package-validation");

        try
        {
            var packageVersion = $"0.0.0-nativeaot-package-validation-{Guid.NewGuid():N}";
            var sqliteProjectPath = FindProjectPath();
            var projectDirectory = Path.GetDirectoryName(sqliteProjectPath)!;
            var nativeAotProjectPath = Path.Combine(
                projectDirectory,
                "..",
                "Turso.Data.Sqlite.NativeAot",
                "Turso.Data.Sqlite.NativeAot.csproj");
            var rawProjectPath = Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj");

            BuildForPackage(sqliteProjectPath, "net8.0");
            Pack(sqliteProjectPath, packageDirectory, packageVersion, "net8.0");
            PackRawPackage(rawProjectPath, packageDirectory, packageVersion);
            Restore(nativeAotProjectPath, packageDirectory, packageVersion);
            BuildNativeAotPackage(nativeAotProjectPath, packageVersion);
            PackNativeAotPackage(nativeAotProjectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(
                packageDirectory,
                $"Turso.Data.Sqlite.NativeAot.win-x64.{packageVersion}.nupkg");
            File.Exists(packagePath).Should().BeTrue();
            AssertPackageMetadata(packagePath, NativeAotPackageDescription);

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                archive.Entries.Should().NotContain(entry =>
                    entry.FullName.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith("Turso.Raw.dll", StringComparison.OrdinalIgnoreCase),
                    "the static NativeAOT package must not duplicate dynamic native assets");

                var nuspecEntry = archive.Entries.Single(entry =>
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                using var nuspecStream = nuspecEntry.Open();
                var nuspec = XDocument.Load(nuspecStream);
                var dependencies = nuspec
                    .Descendants()
                    .Where(element => element.Name.LocalName == "dependency")
                    .ToDictionary(
                        element => element.Attribute("id")!.Value,
                        element => element.Attribute("version")!.Value,
                        StringComparer.Ordinal);

                dependencies["Turso.Data.Sqlite"].Should().Be(packageVersion);
                dependencies["Turso.Raw"].Should().Be(packageVersion);
            }

            RestoreNativeAotPackageConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            DeletePackageDirectory(packageDirectory);
        }
    }

    private static WeakReference LoadManagedConnection(string libraryDirectory)
    {
        var loadContext = new AssemblyLoadContext("turso-package-validation", isCollectible: true);
        var loadContextReference = new WeakReference(loadContext);
        var databasePath = Path.Combine(libraryDirectory, $"pool-{Guid.NewGuid():N}.db");
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver =
            (_, assemblyName) => LoadPackageAssembly(loadContext, libraryDirectory, assemblyName);
        loadContext.Resolving += resolver;
        try
        {
            var facadeAssembly = loadContext.LoadFromAssemblyPath(Path.Combine(libraryDirectory, "Turso.Data.Sqlite.dll"));
            var connectionType = facadeAssembly.GetType("Turso.Data.Sqlite.SqliteConnection", throwOnError: true)!;
            using var connection = (IDisposable)Activator.CreateInstance(
                connectionType,
                $"Data Source={databasePath};Pooling=True;Local Provider=Managed")!;

            connectionType.GetMethod("Open")!.Invoke(connection, null);
            connectionType.GetMethod("Close")!.Invoke(connection, null);
            connectionType.GetMethod("Open")!.Invoke(connection, null);
            connectionType.GetMethod("ClearPool")!.Invoke(null, [connection]);
            connectionType.GetMethod("Close")!.Invoke(connection, null);
            connectionType.GetMethod("ClearAllPools")!.Invoke(null, null);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var candidate = databasePath + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            loadContext.Resolving -= resolver;
            loadContext.Unload();
        }

        return loadContextReference;
    }

    private static void AssertPackageMetadata(string packagePath, string expectedDescription)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        archive.Entries.Should().Contain(entry =>
            string.Equals(entry.FullName, "README.md", StringComparison.OrdinalIgnoreCase));

        var nuspecEntry = archive.Entries.Single(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var nuspecStream = nuspecEntry.Open();
        var metadata = XDocument.Load(nuspecStream)
            .Descendants()
            .Single(element => element.Name.LocalName == "metadata");
        metadata.Elements()
            .Single(element => element.Name.LocalName == "description")
            .Value
            .Should()
            .Be(expectedDescription);
        metadata.Elements()
            .Single(element => element.Name.LocalName == "readme")
            .Value
            .Should()
            .Be("README.md");
    }

    private static void EnsureUnloaded(WeakReference loadContextReference)
    {
        for (var attempt = 0; loadContextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }

        Assert.That(loadContextReference.IsAlive, Is.False, "The package validation AssemblyLoadContext did not unload.");
    }

    private static string Pack(
        string projectPath,
        string packageDirectory,
        string packageVersion,
        string? targetFrameworks = "net9.0")
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            },
        };
        process.StartInfo.ArgumentList.Add("pack");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add("Debug");
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add(packageDirectory);
        if (targetFrameworks is not null)
            process.StartInfo.ArgumentList.Add($"-p:TursoTargetFrameworks={targetFrameworks}");
        process.StartInfo.ArgumentList.Add($"-p:PackageVersion={packageVersion}");

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        var result = output.Result + Environment.NewLine + error.Result;
        Assert.That(process.ExitCode, Is.EqualTo(0), result);
        return result;
    }

    private static void PackRawPackage(string projectPath, string packageDirectory, string packageVersion)
    {
        BuildForPackage(projectPath);
        var output = Pack(projectPath, packageDirectory, packageVersion, targetFrameworks: null);
        Assert.That(output, Does.Not.Contain("NU5128"));
        Assert.That(output, Does.Not.Contain("NU5130"));
    }

    private static void BuildForPackage(string projectPath, string? targetFrameworks = null)
    {
        var arguments = new List<string>
        {
            "build",
            projectPath,
            "--configuration",
            "Debug",
            "--no-restore",
        };
        if (targetFrameworks is not null)
            arguments.Add($"-p:TursoTargetFrameworks={targetFrameworks}");

        RunDotnet(Path.GetDirectoryName(projectPath)!, arguments.ToArray());
    }

    private static void Restore(
        string projectPath,
        string? packageSource = null,
        string? packageVersion = null)
    {
        var arguments = new List<string> { "restore", projectPath, "--force-evaluate" };
        if (packageSource is not null)
        {
            arguments.Add("--source");
            arguments.Add(packageSource);
        }

        if (packageVersion is not null)
            arguments.Add($"-p:PackageVersion={packageVersion}");

        RunDotnet(Path.GetDirectoryName(projectPath)!, arguments.ToArray());
    }

    private static void BuildNativeCompanion(string projectPath)
        => RunDotnet(
            Path.GetDirectoryName(projectPath)!,
            "build",
            projectPath,
            "--configuration",
            "Debug",
            "--no-restore",
            "--no-dependencies",
            "-p:TursoTargetFrameworks=net9.0");

    private static void BuildNativeAotPackage(string projectPath, string packageVersion)
        => RunDotnet(
            Path.GetDirectoryName(projectPath)!,
            "build",
            projectPath,
            "--configuration",
            "Debug",
            "--no-restore",
            "-p:NativeAotRid=win-x64",
            "-p:RequireNativeAssetsForPack=false",
            $"-p:PackageVersion={packageVersion}");

    private static void PackNativeAotPackage(
        string projectPath,
        string packageDirectory,
        string packageVersion)
        => RunDotnet(
            Path.GetDirectoryName(projectPath)!,
            "pack",
            projectPath,
            "--configuration",
            "Debug",
            "--no-build",
            "--no-restore",
            "--output",
            packageDirectory,
            "-p:NativeAotRid=win-x64",
            "-p:RequireNativeAssetsForPack=false",
            $"-p:PackageVersion={packageVersion}");

    private static void RunNativePackageConsumer(string packageDirectory, string packageVersion)
    {
        var consumerDirectory = Path.Combine(packageDirectory, "native-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "NativeConsumer.csproj");
        File.WriteAllText(
            projectPath,
            $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <OutputType>Exe</OutputType>
                  <TargetFramework>net9.0</TargetFramework>
                  <ImplicitUsings>enable</ImplicitUsings>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Turso.Data.Sqlite" Version="{{packageVersion}}" />
                  <PackageReference Include="Turso.Data.Sqlite.Native" Version="{{packageVersion}}" />
                </ItemGroup>
              </Project>
              """);
        File.WriteAllText(
            Path.Combine(consumerDirectory, "Program.cs"),
            """
            using Turso.Data.Sqlite;

            using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            if (command.ExecuteScalar() is not 1L)
                throw new InvalidOperationException("The native companion package did not route Local Provider=Native.");
            """);

        RunDotnet(consumerDirectory, "restore", projectPath, "--source", packageDirectory);
        RunDotnet(consumerDirectory, "run", "--no-restore", "--project", projectPath);
    }

    private static void RunSyncPackageConsumer(string packageDirectory, string packageVersion)
    {
        var consumerDirectory = Path.Combine(packageDirectory, "sync-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "SyncConsumer.csproj");
        File.WriteAllText(
            projectPath,
            $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <OutputType>Exe</OutputType>
                  <TargetFramework>net9.0</TargetFramework>
                  <ImplicitUsings>enable</ImplicitUsings>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Turso.Data.Sqlite.Sync" Version="{{packageVersion}}" />
                </ItemGroup>
              </Project>
              """);
        File.WriteAllText(
            Path.Combine(consumerDirectory, "Program.cs"),
            """
            using Turso;
            using Turso.Data.Sync;

            ReplicaProviderRegistration.Register();
            var factoryType = typeof(ReplicaProviderRegistration).Assembly.GetType(
                "Turso.Data.Sync.SyncReplicaProviderFactory",
                throwOnError: true)!;
            var factory = (TursoReplicaProviderFactory)Activator.CreateInstance(
                factoryType,
                nonPublic: true)!;
            using var replica = factory.OpenReplica(
                new TursoReplicaOptions(
                    ":memory:",
                    new Uri("http://127.0.0.1:1"),
                    authToken: null,
                    bootstrapIfEmpty: false));
            using var statement = replica.PrepareStatement("SELECT 42");
            if (!statement.Read() || statement.GetValue(0).IntValue != 42 || statement.Read())
                throw new InvalidOperationException("The packed Sync companion did not open its local replica.");
            """);

        RunDotnet(consumerDirectory, "restore", projectPath, "--source", packageDirectory);
        using (var assetsStream = File.OpenRead(Path.Combine(consumerDirectory, "obj", "project.assets.json")))
        using (var assets = JsonDocument.Parse(assetsStream))
        {
            var libraries = assets.RootElement.GetProperty("libraries");
            libraries.TryGetProperty($"Turso.Data.Sqlite.Sync/{packageVersion}", out _).Should().BeTrue();
            libraries.TryGetProperty($"Turso.Data.Sqlite/{packageVersion}", out _).Should().BeTrue(
                "the Sync package must restore its matching managed facade");
        }

        RunDotnet(consumerDirectory, "run", "--no-restore", "--project", projectPath);
    }

    private static void RestoreNativeAotPackageConsumer(string packageDirectory, string packageVersion)
    {
        var consumerDirectory = Path.Combine(packageDirectory, "nativeaot-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "NativeAotConsumer.csproj");
        File.WriteAllText(
            projectPath,
            $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <TargetFramework>net8.0</TargetFramework>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Turso.Data.Sqlite.NativeAot.win-x64" Version="{{packageVersion}}" />
                </ItemGroup>
              </Project>
              """);

        RunDotnet(consumerDirectory, "restore", projectPath, "--source", packageDirectory);

        using var assetsStream = File.OpenRead(Path.Combine(consumerDirectory, "obj", "project.assets.json"));
        using var assets = JsonDocument.Parse(assetsStream);
        var libraries = assets.RootElement.GetProperty("libraries");
        foreach (var packageId in new[]
                 {
                     "Turso.Data.Sqlite.NativeAot.win-x64",
                     "Turso.Data.Sqlite",
                     "Turso.Raw",
                 })
        {
            libraries.TryGetProperty($"{packageId}/{packageVersion}", out _).Should().BeTrue(
                $"{packageId} must be restored through the NativeAOT package closure");
        }
    }

    private static void RunManagedPackageConsumer(string packageDirectory, string packageVersion)
    {
        var consumerDirectory = Path.Combine(packageDirectory, "managed-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "ManagedConsumer.csproj");
        File.WriteAllText(
            projectPath,
            $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <OutputType>Exe</OutputType>
                  <TargetFramework>net9.0</TargetFramework>
                  <ImplicitUsings>enable</ImplicitUsings>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Turso.Data.Sqlite" Version="{{packageVersion}}" />
                  <PackageReference Include="Turso.EntityFrameworkCore.Sqlite" Version="{{packageVersion}}" />
                </ItemGroup>
              </Project>
              """);
        File.WriteAllText(
            Path.Combine(consumerDirectory, "Program.cs"),
            """
            using Microsoft.EntityFrameworkCore;
            using Turso.Data.Sqlite;

            const string key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
            var path = Path.Combine(Path.GetTempPath(), $"turso-package-artifact-{Guid.NewGuid():N}.db");
            var connectionString =
                $"Data Source={path};Local Provider=Managed;Encryption Cipher=AES256GCM;Encryption Key={key}";
            try
            {
                using (var encrypted = new SqliteConnection(connectionString))
                {
                    encrypted.Open();
                    encrypted.ExecuteNonQuery("CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('encrypted');");
                }

                using (var reopened = new SqliteConnection(connectionString))
                {
                    reopened.Open();
                    if (reopened.ExecuteScalar<string>("SELECT value FROM data;") != "encrypted")
                        throw new InvalidOperationException("The packed managed provider did not reopen encrypted data.");
                }

                using var unsupported = new SqliteConnection(
                    $"Data Source={path};Local Provider=Managed;Encryption Cipher=AEGIS256;Encryption Key={key}");
                try
                {
                    unsupported.Open();
                    throw new InvalidOperationException("The packed managed provider accepted AEGIS.");
                }
                catch (NotSupportedException exception) when (
                    exception.Message.Contains("cipher ID 1", StringComparison.Ordinal)
                    && exception.Message.Contains("cipher ID 2", StringComparison.Ordinal))
                {
                }
            }
            finally
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                    File.Delete(path + suffix);
            }

            using var managed = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
            managed.Open();

            var options = new DbContextOptionsBuilder<ManagedConsumerContext>()
                .UseTurso(managed)
                .Options;
            await using (var context = new ManagedConsumerContext(options))
            {
                await context.Database.EnsureCreatedAsync();
                context.Records.Add(new ManagedConsumerRecord { Value = "entity-framework" });
                await context.SaveChangesAsync();
                if ((await context.Records.SingleAsync()).Value != "entity-framework")
                    throw new InvalidOperationException("The managed Entity Framework package consumer returned an unexpected result.");
            }

            using var native = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
            try
            {
                native.Open();
            }
            catch (NotSupportedException exception) when (
                exception.Message.Contains("Turso.Data.Sqlite.Native", StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException("The managed package unexpectedly activated a native provider.");

            sealed class ManagedConsumerContext(DbContextOptions<ManagedConsumerContext> options) : DbContext(options)
            {
                public DbSet<ManagedConsumerRecord> Records => Set<ManagedConsumerRecord>();
            }

            sealed class ManagedConsumerRecord
            {
                public int Id { get; init; }

                public required string Value { get; init; }
            }
            """);

        var nugetConfigPath = Path.Combine(consumerDirectory, "NuGet.config");
        File.WriteAllText(
            nugetConfigPath,
            $$"""
              <?xml version="1.0" encoding="utf-8"?>
              <configuration>
                <packageSources>
                  <clear />
                  <add key="managed-package" value="{{System.Security.SecurityElement.Escape(packageDirectory)}}" />
                  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
              </configuration>
              """);
        RunDotnet(consumerDirectory, "restore", projectPath, "--configfile", nugetConfigPath);
        AssertManagedConsumerRestoresNoNativeCompanions(consumerDirectory);
        RunDotnet(consumerDirectory, "build", projectPath, "--no-restore");
        RunDotnet(consumerDirectory, "run", projectPath, "--no-build", "--no-restore");

        var publishDirectory = Path.Combine(consumerDirectory, "publish");
        RunDotnet(
            consumerDirectory,
            "publish",
            projectPath,
            "--configuration",
            "Debug",
            "--no-build",
            "--no-restore",
            "--output",
            publishDirectory);
        AssertManagedConsumerPublishOutputHasNoNativeAssets(publishDirectory);
    }

    private static void RunManagedReplicaOptionsConsumer(string packageDirectory, string packageVersion)
    {
        var consumerDirectory = Path.Combine(packageDirectory, "managed-replica-options-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "ManagedReplicaOptionsConsumer.csproj");
        File.WriteAllText(
            projectPath,
            $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <OutputType>Exe</OutputType>
                  <TargetFramework>net9.0</TargetFramework>
                  <ImplicitUsings>enable</ImplicitUsings>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Turso.Data.Sqlite" Version="{{packageVersion}}" />
                </ItemGroup>
              </Project>
              """);
        File.WriteAllText(
            Path.Combine(consumerDirectory, "Program.cs"),
            """
            using Turso;

            var options = new TursoReplicaOptions(
                "replica.db",
                new Uri("https://example.turso.io"),
                authToken: null)
            {
                LongPollTimeout = TimeSpan.FromSeconds(15),
                PartialBootstrap = TursoPartialBootstrapOptions.Prefix(64 * 1024),
                PushOperationsThreshold = 1000,
                PullBytesThreshold = 1024 * 1024,
            };
            using var nullConnection = new TursoConnection(null!);
            using var defaultConnection = new TursoConnection(default!);
            using var replica = TursoConnection.CreateReplica(options);
            try
            {
                replica.Open();
            }
            catch (NotSupportedException exception) when (
                exception.Message.Contains("Turso.Data.Sqlite.Sync", StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException("The managed package unexpectedly activated embedded replica Sync.");
            """);

        var nugetConfigPath = Path.Combine(consumerDirectory, "NuGet.config");
        File.WriteAllText(
            nugetConfigPath,
            $$"""
              <?xml version="1.0" encoding="utf-8"?>
              <configuration>
                <packageSources>
                  <clear />
                  <add key="managed-package" value="{{System.Security.SecurityElement.Escape(packageDirectory)}}" />
                </packageSources>
              </configuration>
              """);
        RunDotnet(consumerDirectory, "restore", projectPath, "--configfile", nugetConfigPath);
        AssertManagedConsumerRestoresNoNativeCompanions(consumerDirectory);
        RunDotnet(consumerDirectory, "run", projectPath, "--no-restore");
    }

    private static void AssertManagedConsumerRestoresNoNativeCompanions(string consumerDirectory)
    {
        using var assetsStream = File.OpenRead(Path.Combine(consumerDirectory, "obj", "project.assets.json"));
        using var assets = JsonDocument.Parse(assetsStream);
        var nativePackages = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(library => library.Name)
            .Where(IsNativeCompanionPackage)
            .ToArray();

        Assert.That(
            nativePackages,
            Is.Empty,
            "a consumer of Turso.Data.Sqlite alone must not restore an optional native companion");
    }

    private static bool IsNativeCompanionPackage(string packageIdentity)
        => packageIdentity.StartsWith("Turso.Raw/", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Native/", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Sync/", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Sqlite.Native/", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Sqlite.NativeAot", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Sqlite.Sync/", StringComparison.OrdinalIgnoreCase);

    private static void AssertManagedConsumerPublishOutputHasNoNativeAssets(string publishDirectory)
    {
        var nativeAssets = Directory
            .EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is
                "Turso.Raw.dll" or
                "Turso.Data.Native.dll" or
                "Turso.Data.Sync.dll" or
                "turso_sdk_kit.dll" or
                "turso_sync_sdk_kit.dll" or
                "libturso_sdk_kit.so" or
                "libturso_sync_sdk_kit.so" or
                "libturso_sdk_kit.dylib" or
                "libturso_sync_sdk_kit.dylib" or
                "libturso_sdk_kit.a")
            .ToArray();

        Assert.That(
            nativeAssets,
            Is.Empty,
            "publishing a consumer of Turso.Data.Sqlite alone must not emit native companion assets");
    }

    private static void RunDotnet(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            },
        };
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        Assert.That(process.ExitCode, Is.EqualTo(0), output.Result + Environment.NewLine + error.Result);
    }

    private static void DeletePackageDirectory(string packageDirectory)
    {
        const int maxAttempts = 100;
        IOException? lastError = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(packageDirectory, recursive: true);
                return;
            }
            catch (IOException exception) when (attempt < maxAttempts - 1)
            {
                lastError = exception;
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(100);
            }
        }

        throw lastError ?? new IOException($"Unable to delete package directory '{packageDirectory}'.");
    }

    private static string CreatePackageDirectory(string prefix)
    {
        var packageDirectory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);
        return packageDirectory;
    }

    private static Assembly? LoadPackageAssembly(
        AssemblyLoadContext loadContext,
        string libraryDirectory,
        AssemblyName assemblyName)
    {
        var path = Path.Combine(libraryDirectory, $"{assemblyName.Name}.dll");
        return File.Exists(path) ? loadContext.LoadFromAssemblyPath(path) : null;
    }

    private static string FindProjectPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var projectPath = Path.Combine(
                directory.FullName,
                "bindings",
                "dotnet",
                "src",
                "Turso.Data.Sqlite",
                "Turso.Data.Sqlite.csproj");
            if (File.Exists(projectPath))
                return projectPath;
        }

        throw new DirectoryNotFoundException("Could not locate Turso.Data.Sqlite.csproj.");
    }
}
