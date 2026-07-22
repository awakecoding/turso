using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using AwesomeAssertions;

namespace Turso.Tests;

public class TursoDataSqlitePackageArtifactReleaseTests
{
    [Test]
    public void PackageContainsManagedDependenciesAndLoadsManagedConnection()
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

            foreach (var file in new[]
                     {
                         "Turso.Data.Sqlite.dll",
                         "Turso.Data.dll",
                         "Turso.Data.xml",
                         "Turso.Raw.dll",
                         "Turso.Raw.xml",
                         "Turso.Core.dll",
                         "Turso.Core.xml",
                     })
            {
                File.Exists(Path.Combine(libraryDirectory, file)).Should().BeTrue($"{file} must be present for managed package consumers");
            }

            LoadManagedConnection(libraryDirectory);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    private static void LoadManagedConnection(string libraryDirectory)
    {
        var loadContext = new AssemblyLoadContext("turso-package-validation", isCollectible: true);
        try
        {
            loadContext.Resolving += (_, assemblyName) => LoadPackageAssembly(loadContext, libraryDirectory, assemblyName);

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
            loadContext.Unload();
        }
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
