using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace DotRocks.PackageTests;

public sealed class PackageContentTests
{
    private static readonly string[] RuntimePackageIds =
    [
        "DotRocks.Data",
        "DotRocks.EntityFrameworkCore",
        "DotRocks.EntityFrameworkCore.Design",
    ];

    private static readonly string[] AnalyzerPackageIds =
    [
        "DotRocks.Analyzers",
        "DotRocks.Analyzers.CodeFixes",
    ];

    [Fact]
    public async Task Packages_HaveExpectedContentAndMetadata()
    {
        string packageDirectory = await PackSourcePackagesAsync().ConfigureAwait(true);

        foreach (string packageId in RuntimePackageIds)
        {
            string packagePath = SingleRegularPackage(packageDirectory, packageId);
            using ZipArchive archive = await ZipFile
                .OpenReadAsync(packagePath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains("README.md", entries);
            Assert.Contains("lib/net10.0/" + packageId + ".dll", entries);
            Assert.DoesNotContain(
                entries,
                entry => entry.StartsWith("analyzers/", StringComparison.Ordinal)
            );
            await AssertPackageMetadataAsync(
                    archive,
                    packageId,
                    expectDependencies: packageId != "DotRocks.Data"
                )
                .ConfigureAwait(true);

            string symbolsPath = SingleSymbolPackage(packageDirectory, packageId);
            using ZipArchive symbols = await ZipFile
                .OpenReadAsync(symbolsPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Contains(
                "lib/net10.0/" + packageId + ".pdb",
                symbols.Entries.Select(entry => entry.FullName)
            );
            AssertSourceLinkFileExists(packageId);
        }

        foreach (string packageId in AnalyzerPackageIds)
        {
            string packagePath = SingleRegularPackage(packageDirectory, packageId);
            using ZipArchive archive = await ZipFile
                .OpenReadAsync(packagePath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains("README.md", entries);
            Assert.Contains("analyzers/dotnet/cs/" + packageId + ".dll", entries);
            Assert.DoesNotContain(
                entries,
                entry => entry.StartsWith("lib/", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(
                entries,
                entry => entry.StartsWith("ref/", StringComparison.Ordinal)
            );
            await AssertPackageMetadataAsync(archive, packageId, expectDependencies: false)
                .ConfigureAwait(true);
            Assert.Empty(Directory.GetFiles(packageDirectory, packageId + ".*.snupkg"));
        }
    }

    [Fact]
    public void ThirdPartyNotices_ListCentralPackageDependencies()
    {
        string root = FindRepositoryRoot();
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        string[] packageIds = packages
            .Descendants("PackageVersion")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string packageId in packageIds)
        {
            Assert.Contains(packageId, notices, StringComparison.Ordinal);
        }
    }

    private static async Task AssertPackageMetadataAsync(
        ZipArchive archive,
        string packageId,
        bool expectDependencies
    )
    {
        ZipArchiveEntry nuspecEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
        );
        using Stream nuspecStream = await nuspecEntry
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        XDocument nuspec = XDocument.Load(nuspecStream);

        Assert.Equal(packageId, NuspecValue(nuspec, "id"));
        Assert.Equal("MIT", NuspecValue(nuspec, "license"));
        Assert.Equal(
            "https://github.com/dotrocks/dotrocks",
            NuspecValue(nuspec, "repository", "url")
        );
        if (expectDependencies)
        {
            Assert.Contains(
                nuspec.Descendants(),
                element => element.Name.LocalName == "dependency"
            );
        }
        else
        {
            Assert.DoesNotContain(
                nuspec.Descendants(),
                element => element.Name.LocalName == "dependency"
            );
        }
    }

    private static string? NuspecValue(
        XDocument document,
        string localName,
        string? attribute = null
    )
    {
        XElement? element = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName);
        return attribute is null ? element?.Value : element?.Attribute(attribute)?.Value;
    }

    private static void AssertSourceLinkFileExists(string packageId)
    {
        string root = FindRepositoryRoot();
        string projectName = packageId;
        string sourceLinkPath = Path.Combine(
            root,
            "src",
            projectName,
            "obj",
            "Release",
            "net10.0",
            projectName + ".sourcelink.json"
        );
        Assert.True(File.Exists(sourceLinkPath), sourceLinkPath);
        string sourceLink = File.ReadAllText(sourceLinkPath);
        Assert.Contains("documents", sourceLink, StringComparison.Ordinal);
    }

    private static bool IsRegularPackage(string path, string packageId)
    {
        string fileName = Path.GetFileName(path);
        string expectedPrefix = packageId + ".";
        return fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && fileName.Length > expectedPrefix.Length
            && char.IsDigit(fileName[expectedPrefix.Length])
            && !fileName.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains(".symbols.", StringComparison.OrdinalIgnoreCase);
    }

    private static string SingleRegularPackage(string packageDirectory, string packageId)
    {
        string[] packages = Directory
            .GetFiles(packageDirectory, packageId + ".*.nupkg")
            .Where(path => IsRegularPackage(path, packageId))
            .ToArray();
        Assert.True(
            packages.Length == 1,
            "Regular packages: " + string.Join(", ", packages.Select(Path.GetFileName))
        );
        return packages[0];
    }

    private static string SingleSymbolPackage(string packageDirectory, string packageId)
    {
        string[] packages = Directory
            .GetFiles(packageDirectory, packageId + ".*.snupkg")
            .Where(path => IsSymbolPackage(path, packageId))
            .ToArray();
        Assert.True(
            packages.Length == 1,
            "Symbol packages: " + string.Join(", ", packages.Select(Path.GetFileName))
        );
        return packages[0];
    }

    private static bool IsSymbolPackage(string path, string packageId)
    {
        string fileName = Path.GetFileName(path);
        string expectedPrefix = packageId + ".";
        return fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && fileName.Length > expectedPrefix.Length
            && char.IsDigit(fileName[expectedPrefix.Length])
            && fileName.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> PackSourcePackagesAsync()
    {
        string root = FindRepositoryRoot();
        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "dotrocks-package-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outputDirectory);
        string configuration = GetAssemblyConfiguration();
        string[] projectPaths =
        [
            Path.Combine(root, "src", "DotRocks.Data", "DotRocks.Data.csproj"),
            Path.Combine(
                root,
                "src",
                "DotRocks.EntityFrameworkCore",
                "DotRocks.EntityFrameworkCore.csproj"
            ),
            Path.Combine(
                root,
                "src",
                "DotRocks.EntityFrameworkCore.Design",
                "DotRocks.EntityFrameworkCore.Design.csproj"
            ),
            Path.Combine(root, "src", "DotRocks.Analyzers", "DotRocks.Analyzers.csproj"),
            Path.Combine(
                root,
                "src",
                "DotRocks.Analyzers.CodeFixes",
                "DotRocks.Analyzers.CodeFixes.csproj"
            ),
        ];

        foreach (string projectPath in projectPaths)
        {
            CommandResult result = await RunDotnetAsync(
                    $"pack \"{projectPath}\" --configuration {configuration} --no-build --disable-build-servers --output \"{outputDirectory}\""
                )
                .ConfigureAwait(true);

            Assert.Equal(0, result.ExitCode);
        }

        return outputDirectory;
    }

    private static async Task<CommandResult> RunDotnetAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FindRepositoryRoot(),
        };
        process.StartInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                TestContext.Current.CancellationToken
            );
        await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(true);
        return new CommandResult(process.ExitCode, output + error);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotRocks.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DotRocks.slnx.");
    }

    private static string GetAssemblyConfiguration() =>
        typeof(PackageContentTests)
            .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
        ?? "Debug";

    private sealed record CommandResult(int ExitCode, string Output);
}
