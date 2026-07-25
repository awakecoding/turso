using System.Xml.Linq;
using AwesomeAssertions;

namespace Turso.Tests;

public sealed class SyncMobilePackageBuildTargetsTests
{
    [Test]
    public void SyncPackageBuildTargetsSelectEveryMobileAsset()
    {
        var syncProjectPath = FindSyncProjectPath();
        var syncProjectDirectory = Path.GetDirectoryName(syncProjectPath)!;
        var targetsPath = Path.Combine(
            syncProjectDirectory,
            "buildTransitive",
            "Turso.Data.Sqlite.Sync.targets");
        var targets = XDocument.Load(targetsPath);

        var androidAssets = targets
            .Descendants("AndroidNativeLibrary")
            .ToDictionary(
                element => element.Element("Abi")!.Value,
                element => element.Attribute("Include")!.Value);
        androidAssets.Should().BeEquivalentTo(
            new Dictionary<string, string>
            {
                ["armeabi-v7a"] = "$(_TursoSyncPackageRoot)runtimes\\android-arm\\native\\libturso_sync_sdk_kit.so",
                ["arm64-v8a"] = "$(_TursoSyncPackageRoot)runtimes\\android-arm64\\native\\libturso_sync_sdk_kit.so",
                ["x86"] = "$(_TursoSyncPackageRoot)runtimes\\android-x86\\native\\libturso_sync_sdk_kit.so",
                ["x86_64"] = "$(_TursoSyncPackageRoot)runtimes\\android-x64\\native\\libturso_sync_sdk_kit.so",
            });

        targets.Descendants("NativeReference")
            .Should()
            .ContainSingle()
            .Which
            .Attribute("Include")!
            .Value
            .Should()
            .Be("$(_TursoSyncPackageRoot)runtimes\\ios-universal\\native\\libturso_sync_sdk_kit.xcframework");

        var project = XDocument.Load(syncProjectPath);
        var targetsPackageItem = project.Descendants("None").Single(element =>
            string.Equals(
                element.Attribute("Include")?.Value,
                "buildTransitive\\Turso.Data.Sqlite.Sync.targets",
                StringComparison.Ordinal));
        targetsPackageItem.Attribute("Pack")!.Value.Should().Be("true");
        targetsPackageItem.Attribute("PackagePath")!.Value.Should()
            .Be("buildTransitive\\Turso.Data.Sqlite.Sync.targets");
    }

    private static string FindSyncProjectPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var projectPath = Path.Combine(
                directory.FullName,
                "bindings",
                "dotnet",
                "src",
                "Turso.Data.Sync",
                "Turso.Data.Sync.csproj");
            if (File.Exists(projectPath))
                return projectPath;
        }

        throw new DirectoryNotFoundException("Could not locate Turso.Data.Sync.csproj.");
    }
}
