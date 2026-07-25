using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Turso.Tests;

public sealed class NativePlatformSupportMatrixTests
{
    private static readonly string[] DesktopRids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    ];

    private static readonly string[] SupportedTargetFrameworks =
    [
        "net8.0",
        "net9.0",
        "net10.0",
    ];

    [Test]
    public void NativeAotPackageDefinesOnlyTheReleaseGatedDesktopMatrix()
    {
        var dotnetDirectory = FindDotnetDirectory();
        var packageProject = XDocument.Load(Path.Combine(
            dotnetDirectory,
            "src",
            "Turso.Data.Sqlite.NativeAot",
            "Turso.Data.Sqlite.NativeAot.csproj"));
        var packageTargets = XDocument.Load(Path.Combine(
            dotnetDirectory,
            "src",
            "Turso.Data.Sqlite.NativeAot",
            "buildTransitive",
            "Turso.Data.Sqlite.NativeAot.targets"));

        packageProject
            .Descendants("NativeAotTarget")
            .Select(element => ExtractComparedValue(element.Attribute("Condition")!.Value, "NativeAotRid"))
            .Should()
            .BeEquivalentTo(DesktopRids);

        var properties = packageTargets.Descendants("PropertyGroup").Elements().ToArray();
        var supportedRidCondition = properties
            .Single(element => element.Name.LocalName == "_TursoDataSqliteStaticNativeSupportedRid")
            .Attribute("Condition")!
            .Value;
        ExtractComparedValues(supportedRidCondition, "RuntimeIdentifier")
            .Should()
            .BeEquivalentTo(DesktopRids);

        var supportedTargetFrameworkCondition = properties
            .Single(element => element.Name.LocalName == "_TursoDataSqliteStaticNativeSupportedTargetFramework")
            .Attribute("Condition")!
            .Value;
        ExtractComparedValues(supportedTargetFrameworkCondition, "TargetFramework")
            .Should()
            .BeEquivalentTo(SupportedTargetFrameworks);

        var mobileTargetFrameworkCondition = properties
            .Single(element => element.Name.LocalName == "_TursoDataSqliteStaticNativeMobileTargetFramework")
            .Attribute("Condition")!
            .Value;
        foreach (var platform in new[] { "android", "ios", "maccatalyst", "tvos" })
            mobileTargetFrameworkCondition.Should().Contain($"== '{platform}'");

        packageTargets
            .Descendants("Error")
            .Select(element => element.Attribute("Text")?.Value)
            .Should()
            .Contain(text => text != null && text.Contains("does not support mobile target framework", StringComparison.Ordinal));
    }

    [Test]
    public void IosPackagesCarryAUniversalArm64AndX64SimulatorSlice()
    {
        var dotnetDirectory = FindDotnetDirectory();
        var makefile = File.ReadAllText(Path.Combine(dotnetDirectory, "Makefile"));
        var workflow = File.ReadAllText(Path.Combine(
            Directory.GetParent(Directory.GetParent(dotnetDirectory)!.FullName)!.FullName,
            ".github",
            "workflows",
            "dotnet-publish.yml"));
        var rawProject = File.ReadAllText(Path.Combine(dotnetDirectory, "src", "Turso.Raw", "Turso.Raw.csproj"));
        var syncProject = File.ReadAllText(Path.Combine(dotnetDirectory, "src", "Turso.Data.Sync", "Turso.Data.Sync.csproj"));

        makefile.Should().Contain("build-rust-iossimulator64:");
        makefile.Should().Contain("--target x86_64-apple-ios");
        makefile.Should().Contain(
            "lipo -create ./rs_compiled/aarch64-apple-ios-sim/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) ./rs_compiled/x86_64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE)");
        makefile.Should().Contain(
            "lipo -create ./rs_compiled/aarch64-apple-ios-sim/$(RUST_PROFILE_DIR)/$(IOS_SYNC_DYLIB_FILE) ./rs_compiled/x86_64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_SYNC_DYLIB_FILE)");

        workflow.Should().Contain("artifact-name: native-iossimulator-x64");
        workflow.Should().Contain("target: x86_64-apple-ios");
        workflow.Should().Contain("-Target ios-simulator-universal");

        foreach (var project in new[] { rawProject, syncProject })
        {
            project.Should().Contain("ios-arm64_x86_64-simulator");
            project.Should().NotContain("ios-arm64-simulator");
        }
    }

    [Test]
    public void ReleaseWorkflowPreservesSixDesktopGatesAcrossEverySupportedTfm()
    {
        var repositoryRoot = Directory.GetParent(Directory.GetParent(FindDotnetDirectory())!.FullName)!.FullName;
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "dotnet-publish.yml"));
        var dynamicSection = Slice(
            workflow,
            "  validate-dynamic-native-packages:",
            "  validate-nativeaot-static-package:");
        var nativeAotSection = Slice(
            workflow,
            "  validate-nativeaot-static-package:",
            "  publish-managed-to-nuget:");

        ExtractMatrixRids(dynamicSection).Should().BeEquivalentTo(DesktopRids);
        ExtractMatrixRids(nativeAotSection).Should().BeEquivalentTo(DesktopRids);
        foreach (var targetFramework in SupportedTargetFrameworks)
        {
            dynamicSection.Should().Contain($"\"{targetFramework}\"");
            nativeAotSection.Should().Contain($"\"{targetFramework}\"");
        }

        nativeAotSection.Should().Contain("Validate-NativeArtifact.ps1");
        nativeAotSection.Should().Contain("-DynamicArtifacts $executable");
    }

    [Test]
    public void ArtifactGateDefinesArchitectureDependencyAndSigningPolicies()
    {
        var dotnetDirectory = FindDotnetDirectory();
        var validator = File.ReadAllText(Path.Combine(
            dotnetDirectory,
            "scripts",
            "Validate-NativeArtifact.ps1"));
        var workflow = File.ReadAllText(Path.Combine(
            Directory.GetParent(Directory.GetParent(dotnetDirectory)!.FullName)!.FullName,
            ".github",
            "workflows",
            "dotnet-publish.yml"));

        foreach (var target in new[]
                 {
                     "x86_64-pc-windows-msvc",
                     "aarch64-pc-windows-msvc",
                     "x86_64-unknown-linux-gnu",
                     "aarch64-unknown-linux-gnu",
                     "aarch64-linux-android",
                     "armv7-linux-androideabi",
                     "x86_64-linux-android",
                     "i686-linux-android",
                     "x86_64-apple-darwin",
                     "aarch64-apple-darwin",
                     "aarch64-apple-ios",
                     "aarch64-apple-ios-sim",
                     "x86_64-apple-ios",
                 })
        {
            validator.Should().Contain($"\"{target}\"");
        }

        validator.Should().Contain("Get-AuthenticodeSignature");
        validator.Should().Contain("vswhere.exe");
        validator.Should().Contain("readelf");
        validator.Should().Contain("otool");
        validator.Should().Contain("codesign --verify --strict");
        workflow.Should().Contain("Require Windows signing configuration for native publication");
        workflow.Should().Contain("Validate native architecture, dependencies, and signatures");
        workflow.Should().Contain("REQUIRE_WINDOWS_SIGNATURE");
    }

    private static string FindDotnetDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bindings", "dotnet");
            if (File.Exists(Path.Combine(candidate, "Makefile")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate bindings/dotnet.");
    }

    private static string ExtractComparedValue(string condition, string property)
        => ExtractComparedValues(condition, property).Single();

    private static string[] ExtractComparedValues(string condition, string property)
        => Regex.Matches(condition, $@"'\$\({Regex.Escape(property)}\)'\s*==\s*'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string[] ExtractMatrixRids(string workflowSection)
        => Regex.Matches(workflowSection, @"(?m)^\s+rid:\s+(\S+)\s*$")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string Slice(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        var endIndex = value.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        startIndex.Should().BeGreaterThanOrEqualTo(0);
        endIndex.Should().BeGreaterThan(startIndex);
        return value[startIndex..endIndex];
    }
}
