using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using AwesomeAssertions;

namespace Turso.Tests;

public class TursoDataSqlitePackageArtifactReleaseTests
{
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
                        entry.FullName.Contains("turso_sdk_kit", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("Turso.Raw", StringComparison.OrdinalIgnoreCase),
                        "the managed package must not carry native implementation assets or assemblies");
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
            Pack(Path.Combine(projectDirectory, "..", "Turso.Raw", "Turso.Raw.csproj"), packageDirectory, packageVersion);
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

    private static void Pack(string projectPath, string packageDirectory, string packageVersion)
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
        process.StartInfo.ArgumentList.Add("-p:TursoTargetFrameworks=net9.0");
        process.StartInfo.ArgumentList.Add($"-p:PackageVersion={packageVersion}");

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        Assert.That(process.ExitCode, Is.EqualTo(0), output.Result + Environment.NewLine + error.Result);
    }

    private static void Restore(string projectPath)
        => RunDotnet(Path.GetDirectoryName(projectPath)!, "restore", projectPath);

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
        RunDotnet(consumerDirectory, "run", "--no-restore", "--project", projectPath);
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
