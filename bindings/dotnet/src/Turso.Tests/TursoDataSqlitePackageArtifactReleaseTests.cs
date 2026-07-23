using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Turso.Tests;

public class TursoDataSqlitePackageArtifactReleaseTests
{
    private const string RawPackageTargetFrameworks = "net8.0;net9.0;net10.0";

    [Test]
    public void PackageContainsManagedDependenciesWithoutRawAndLoadsManagedConnection()
    {
        var packageDirectory = Path.Combine(AppContext.BaseDirectory, $"turso-package-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);

        try
        {
            var projectPath = FindProjectPath();
            var packageVersion = "0.0.0-package-validation";
            Pack(projectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(packageDirectory, $"Turso.Data.Sqlite.{packageVersion}.nupkg");
            File.Exists(packagePath).Should().BeTrue();

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
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Test]
    public void NativeCompanionPackageRoutesExplicitNativeConnections()
    {
        var packageDirectory = Path.Combine(AppContext.BaseDirectory, $"turso-native-package-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);

        try
        {
            var packageVersion = $"0.0.0-native-package-validation-{Guid.NewGuid():N}";
            var projectDirectory = Path.GetDirectoryName(FindProjectPath())!;
            Pack(Path.Combine(projectDirectory, "Turso.Data.Sqlite.csproj"), packageDirectory, packageVersion);
            PackRawPackage(
                Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj"),
                packageDirectory,
                packageVersion);
            var nativeProjectPath = Path.Combine(projectDirectory, "..", "Turso.Data.Native", "Turso.Data.Native.csproj");
            Restore(nativeProjectPath);
            BuildNativeCompanion(nativeProjectPath);
            Pack(nativeProjectPath, packageDirectory, packageVersion);

            RunNativePackageConsumer(packageDirectory, packageVersion);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Test]
    public void RawPackageContainsManagedClosureForEveryTargetFramework()
    {
        var packageDirectory = Path.Combine(AppContext.BaseDirectory, $"turso-raw-package-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);

        try
        {
            var packageVersion = $"0.0.0-raw-package-validation-{Guid.NewGuid():N}";
            var projectDirectory = Path.GetDirectoryName(FindProjectPath())!;
            var rawProjectPath = Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj");
            PackRawPackage(rawProjectPath, packageDirectory, packageVersion);

            var packagePath = Path.Combine(packageDirectory, $"Turso.Raw.{packageVersion}.nupkg");
            File.Exists(packagePath).Should().BeTrue();

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
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Test]
    public void NativeAotStaticPackageDeclaresManagedFacadeAndRestoresClosure()
    {
        var packageDirectory = Path.Combine(AppContext.BaseDirectory, $"turso-nativeaot-package-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDirectory);

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
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    private static WeakReference LoadManagedConnection(string libraryDirectory)
    {
        var loadContext = new AssemblyLoadContext("turso-package-validation", isCollectible: true);
        var loadContextReference = new WeakReference(loadContext);
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver =
            (_, assemblyName) => LoadPackageAssembly(loadContext, libraryDirectory, assemblyName);
        loadContext.Resolving += resolver;
        try
        {
            var facadeAssembly = loadContext.LoadFromAssemblyPath(Path.Combine(libraryDirectory, "Turso.Data.Sqlite.dll"));
            var connectionType = facadeAssembly.GetType("Turso.Data.Sqlite.SqliteConnection", throwOnError: true)!;
            using var connection = (IDisposable)Activator.CreateInstance(
                connectionType,
                "Data Source=:memory:;Local Provider=Managed")!;

            connectionType.GetMethod("Open")!.Invoke(connection, null);
            connectionType.GetMethod("Close")!.Invoke(connection, null);
        }
        finally
        {
            loadContext.Resolving -= resolver;
            loadContext.Unload();
        }

        return loadContextReference;
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
        var arguments = new List<string> { "restore", projectPath };
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
                </ItemGroup>
              </Project>
              """);
        File.WriteAllText(
            Path.Combine(consumerDirectory, "Program.cs"),
            """
            using Turso.Data.Sqlite;

            using (var managed = new SqliteConnection("Data Source=:memory:;Local Provider=Managed"))
            {
                managed.Open();
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
            """);

        RunDotnet(consumerDirectory, "restore", projectPath, "--source", packageDirectory);
        AssertManagedConsumerRestoresNoNativeCompanions(consumerDirectory);
        RunDotnet(consumerDirectory, "run", "--no-restore", "--project", projectPath);
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
           packageIdentity.StartsWith("Turso.Data.Sqlite.Native/", StringComparison.OrdinalIgnoreCase) ||
           packageIdentity.StartsWith("Turso.Data.Sqlite.NativeAot.", StringComparison.OrdinalIgnoreCase);

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
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        Assert.That(process.ExitCode, Is.EqualTo(0), output.Result + Environment.NewLine + error.Result);
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
