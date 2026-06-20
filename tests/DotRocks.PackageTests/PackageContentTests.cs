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
        AssertExpectedPackageFiles(packageDirectory);

        foreach (string packageId in RuntimePackageIds)
        {
            string packagePath = SingleRegularPackage(packageDirectory, packageId);
            using ZipArchive archive = await ZipFile
                .OpenReadAsync(packagePath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains("README.md", entries);
            Assert.Contains("lib/net10.0/" + packageId + ".dll", entries);
            Assert.Contains("lib/net10.0/" + packageId + ".xml", entries);
            await AssertPackageReadmeAsync(archive).ConfigureAwait(true);
            AssertRuntimePackageLayout(packageId, entries);
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
            Assert.DoesNotContain(
                symbols.Entries.Select(entry => entry.FullName),
                entry => entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
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
            await AssertPackageReadmeAsync(archive).ConfigureAwait(true);
            AssertAnalyzerPackageLayout(packageId, entries);
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
    public async Task Packages_CanBeConsumedFromLocalNuGetSourceAndRunAnalyzers()
    {
        string packageDirectory = await PackSourcePackagesAsync().ConfigureAwait(true);
        string consumerDirectory = Path.Combine(
            Path.GetTempPath(),
            "dotrocks-package-consumer-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(consumerDirectory);

        await WriteConsumerProjectAsync(consumerDirectory, packageDirectory).ConfigureAwait(true);

        CommandResult restore = await RunDotnetAsync(
                "restore Consumer.csproj --configfile NuGet.config --disable-build-servers",
                consumerDirectory
            )
            .ConfigureAwait(true);
        AssertCommandSucceeded(restore);

        CommandResult build = await RunDotnetAsync(
                "build Consumer.csproj --no-restore --disable-build-servers",
                consumerDirectory
            )
            .ConfigureAwait(true);

        AssertCommandSucceeded(build);
        Assert.Contains("DTR0001", build.Output, StringComparison.Ordinal);
        Assert.Contains("DTR0002", build.Output, StringComparison.Ordinal);
        Assert.Contains("DTR0003", build.Output, StringComparison.Ordinal);
        Assert.Contains("DTR0004", build.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("CS8032", build.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("AD0001", build.Output, StringComparison.Ordinal);
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
        Assert.Equal("README.md", NuspecValue(nuspec, "readme"));
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

    private static async Task AssertPackageReadmeAsync(ZipArchive archive)
    {
        ZipArchiveEntry readmeEntry = Assert.Single(
            archive.Entries,
            entry => string.Equals(entry.FullName, "README.md", StringComparison.Ordinal)
        );
        using Stream stream = await readmeEntry
            .OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        string readme = await reader
            .ReadToEndAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains("Verified robustness", readme, StringComparison.Ordinal);
        Assert.Contains("DotRocksDataSource", readme, StringComparison.Ordinal);
        Assert.Contains("DotRocksFactory", readme, StringComparison.Ordinal);
    }

    private static void AssertExpectedPackageFiles(string packageDirectory)
    {
        string[] regularPackages = Directory
            .GetFiles(packageDirectory, "*.nupkg")
            .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        string[] symbolPackages = Directory
            .GetFiles(packageDirectory, "*.snupkg")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RuntimePackageIds.Length + AnalyzerPackageIds.Length, regularPackages.Length);
        Assert.Equal(RuntimePackageIds.Length, symbolPackages.Length);
        Assert.DoesNotContain(
            regularPackages,
            path => Path.GetFileName(path).Contains(".symbols.", StringComparison.OrdinalIgnoreCase)
        );

        string[] regularPackageIds = regularPackages
            .Select(GetPackageIdFromPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] symbolPackageIds = symbolPackages
            .Select(GetPackageIdFromPath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            RuntimePackageIds.Concat(AnalyzerPackageIds).Order(StringComparer.Ordinal).ToArray(),
            regularPackageIds
        );
        Assert.Equal(RuntimePackageIds.Order(StringComparer.Ordinal).ToArray(), symbolPackageIds);
        Assert.DoesNotContain(
            symbolPackageIds,
            packageId => AnalyzerPackageIds.Contains(packageId)
        );
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

    private static void AssertRuntimePackageLayout(string packageId, string[] entries)
    {
        Assert.Equal(
            ["lib/net10.0/" + packageId + ".dll"],
            entries
                .Where(entry =>
                    entry.StartsWith("lib/", StringComparison.Ordinal)
                    && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                )
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.DoesNotContain(entries, entry => entry.StartsWith("ref/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("build/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("buildTransitive/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("runtimes/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("native/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("contentFiles/", StringComparison.Ordinal)
        );
    }

    private static void AssertAnalyzerPackageLayout(string packageId, string[] entries)
    {
        Assert.Equal(
            ["analyzers/dotnet/cs/" + packageId + ".dll"],
            entries
                .Where(entry =>
                    entry.StartsWith("analyzers/", StringComparison.Ordinal)
                    && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                )
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.DoesNotContain(entries, entry => entry.StartsWith("lib/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("ref/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("build/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("buildTransitive/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("runtimes/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("native/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("contentFiles/", StringComparison.Ordinal)
        );
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

    private static string GetPackageIdFromPath(string path)
    {
        string fileName = Path.GetFileName(path);
        string? packageId = RuntimePackageIds
            .Concat(AnalyzerPackageIds)
            .OrderByDescending(id => id.Length)
            .FirstOrDefault(id => fileName.StartsWith(id + ".", StringComparison.Ordinal));
        Assert.NotNull(packageId);
        return packageId;
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

    private static async Task WriteConsumerProjectAsync(
        string consumerDirectory,
        string packageDirectory
    )
    {
        string nugetConfigPath = Path.Combine(consumerDirectory, "NuGet.config");
        string projectPath = Path.Combine(consumerDirectory, "Consumer.csproj");
        string programPath = Path.Combine(consumerDirectory, "Program.cs");

        await File.WriteAllTextAsync(
                nugetConfigPath,
                CreateNuGetConfig(packageDirectory),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        await File.WriteAllTextAsync(
                projectPath,
                CreateConsumerProject(packageDirectory),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);

        await File.WriteAllTextAsync(
                programPath,
                ConsumerProgram,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
    }

    private static string CreateNuGetConfig(string packageDirectory) =>
        new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "local"),
                        new XAttribute("value", packageDirectory)
                    ),
                    new XElement(
                        "add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                        new XAttribute("protocolVersion", "3")
                    )
                ),
                new XElement(
                    "packageSourceMapping",
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "local"),
                        new XElement("package", new XAttribute("pattern", "DotRocks.*"))
                    ),
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "*"))
                    )
                )
            )
        ).ToString();

    private static string CreateConsumerProject(string packageDirectory)
    {
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("Nullable", "enable"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("TreatWarningsAsErrors", "false")
                ),
                new XElement(
                    "ItemGroup",
                    RuntimePackageIds.Select(packageId =>
                        CreatePackageReference(packageDirectory, packageId, privateAssets: false)
                    ),
                    AnalyzerPackageIds.Select(packageId =>
                        CreatePackageReference(packageDirectory, packageId, privateAssets: true)
                    )
                )
            )
        );

        return project.ToString();
    }

    private static XElement CreatePackageReference(
        string packageDirectory,
        string packageId,
        bool privateAssets
    )
    {
        string version = GetPackageVersion(
            SingleRegularPackage(packageDirectory, packageId),
            packageId
        );
        var element = new XElement(
            "PackageReference",
            new XAttribute("Include", packageId),
            new XAttribute("Version", version)
        );
        if (privateAssets)
        {
            element.Add(new XAttribute("PrivateAssets", "all"));
        }

        return element;
    }

    private static string GetPackageVersion(string packagePath, string packageId)
    {
        string fileName = Path.GetFileNameWithoutExtension(packagePath);
        string version = fileName[(packageId.Length + 1)..];
        Assert.True(Version.TryParse(version, out _), "Package version: " + version);
        return version;
    }

    private static void AssertCommandSucceeded(CommandResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"dotnet command failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output}"
            )
        );
    }

    private const string ConsumerProgram = """
        using DotRocks.Data;
        using DotRocks.Data.Loading;
        using Microsoft.EntityFrameworkCore;

        var builder = new DotRocksConnectionStringBuilder(
            "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=https://127.0.0.1:8030"
        );
        if (builder.ToString().Contains("secret", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Connection string builder leaked a password.");
        }

        using var dataSource = new DotRocksDataSource(builder.ConnectionString);
        using var dataSourceConnection = dataSource.CreateConnection();
        using var factoryConnection = DotRocksFactory.Instance.CreateConnection();
        factoryConnection!.ConnectionString = builder.ConnectionString;

        _ = new DotRocksStreamLoadClient(
            "Server=127.0.0.1;User ID=root;Password=secret;Stream Load Endpoint=http://127.0.0.1:8030"
        );

        internal sealed class ConsumerContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Widget>().HasKey(widget => widget.Id);
                modelBuilder.Entity<Widget>().Property(widget => widget.Payload).HasColumnType("varbinary");
            }
        }

        internal sealed class Widget
        {
            public int Id { get; set; }

            public byte[] Payload { get; set; } = [];
        }

        internal static class TransactionConsumer
        {
            public static void Complete(DotRocksTransaction transaction)
            {
                transaction.Commit();
                transaction.Rollback();
            }
        }
        """;

    private static async Task<CommandResult> RunDotnetAsync(
        string arguments,
        string? workingDirectory = null
    )
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? FindRepositoryRoot(),
        };
        process.StartInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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
