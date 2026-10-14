using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>Opens the Skia package produced by the same build as the Skia library under test and checks its contents and dependencies; skips when that library was built in local mode (UseLocalNativeProject=true), which builds no package.</summary>
public class PackageContentTests
{
    private static string Configuration =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    private static string RepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CodeBrix.WebGpu.slnx"))) directory = directory.Parent;
        return directory?.FullName;
    }

    private static readonly Assembly Library = typeof(WebGpuSkiaTarget).Assembly;

    /// <summary>A value the library's csproj stamped as AssemblyMetadata (how this build consumed CodeBrix.WebGpu.Native).</summary>
    private static string LibraryMetadata(string key) =>
        Library.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

    private static bool BuiltInLocalMode => LibraryMetadata("CodeBrix.WebGpu.Native.Reference") == "Project";

    /// <summary>The path of the nupkg the build of the library under test produced: bin/&lt;configuration&gt;/ and the library's own version, in NuGet's normalized form (a zero revision is dropped).</summary>
    private static string ThisBuildsPackage()
    {
        Version version = Library.GetName().Version;
        string normalized = version.Revision > 0 ? version.ToString() : version.ToString(3);
        string root = RepositoryRoot();
        return root == null ? null : Path.Combine(root, "src", "CodeBrix.WebGpu.Skia", "bin", Configuration, "CodeBrix.WebGpu.Skia.MitLicenseForever." + normalized + ".nupkg");
    }

    /// <summary>This build's package; skips in local mode, fails when a package-mode build left no package.</summary>
    private static string PackageUnderTest()
    {
        Assert.SkipWhen(BuiltInLocalMode, "The Skia library was built with UseLocalNativeProject=true (Native as a ProjectReference); local mode never builds a package.");
        string package = ThisBuildsPackage();
        Assert.True(package != null && File.Exists(package), "The package-mode build of the Skia library should have packed " + (package ?? "CodeBrix.WebGpu.Skia.MitLicenseForever.<version>.nupkg") + ".");
        return package;
    }

    [Fact]
    public void library_records_how_it_consumed_the_native_package()
    {
        //Arrange
        string reference = LibraryMetadata("CodeBrix.WebGpu.Native.Reference");

        //Act
        string pinned = LibraryMetadata("CodeBrix.WebGpu.Native.PackageVersion");

        //Assert
        reference.Should().BeOneOf("Project", "Package");
        if (reference == "Package") pinned.Should().NotBeNullOrWhiteSpace();
        else pinned.Should().BeNull();
    }

    [Fact]
    public void package_contains_the_assembly_and_its_own_documents()
    {
        //Arrange
        string package = PackageUnderTest();

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] entries = zip.Entries.Select(entry => entry.FullName).ToArray();
        using StreamReader readme = new StreamReader(zip.GetEntry("README.md").Open());
        string readmeText = readme.ReadToEnd();

        //Assert
        entries.Should().Contain(new[]
        {
            "lib/net10.0/CodeBrix.WebGpu.Skia.dll",
            "lib/net10.0/CodeBrix.WebGpu.Skia.xml",
            "README.md",
            "AGENT-README.txt",
            "THIRD-PARTY-NOTICES.txt",
            "LICENSE",
            "icon-codebrix-128.png",
        });
        entries.Should().NotContain(name => name.StartsWith("runtimes/", StringComparison.Ordinal));
        readmeText.Should().StartWith("# CodeBrix.WebGpu.Skia");
    }

    [Fact]
    public void package_depends_on_SkiaSharp_and_the_native_package_only()
    {
        //Arrange
        string package = PackageUnderTest();

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        ZipArchiveEntry nuspecEntry = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(stream);
        var dependencies = nuspec.Descendants().Where(element => element.Name.LocalName == "dependency")
            .Select(element => new { Id = (string)element.Attribute("id"), Version = (string)element.Attribute("version") })
            .ToArray();

        //Assert
        dependencies.Select(dependency => dependency.Id).Should().BeEquivalentTo(new[] { "SkiaSharp", "CodeBrix.WebGpu.Native.MitLicenseForever" });
        dependencies.Single(dependency => dependency.Id == "SkiaSharp").Version.Should().Be("4.153.1");
        dependencies.Single(dependency => dependency.Id == "CodeBrix.WebGpu.Native.MitLicenseForever").Version.Should().Be(LibraryMetadata("CodeBrix.WebGpu.Native.PackageVersion"));
    }

    [Fact]
    public void package_contains_nothing_but_the_assembly_the_documents()
    {
        //Arrange
        string package = PackageUnderTest();
        string[] expected =
        {
            "_rels/.rels", "[Content_Types].xml", "CodeBrix.WebGpu.Skia.MitLicenseForever.nuspec",
            "lib/net10.0/CodeBrix.WebGpu.Skia.dll", "lib/net10.0/CodeBrix.WebGpu.Skia.xml",
            "README.md", "AGENT-README.txt", "THIRD-PARTY-NOTICES.txt", "LICENSE", "icon-codebrix-128.png",
        };

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] unexpected = zip.Entries.Select(entry => entry.FullName)
            .Where(name => !expected.Contains(name, StringComparer.Ordinal)
                && !name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal))
            .ToArray();

        //Assert
        unexpected.Should().BeEmpty();
    }

    [Fact]
    public void package_documents_are_the_packages_own_files()
    {
        //Arrange
        string package = PackageUnderTest();
        string root = RepositoryRoot();
        (string Packed, string Source)[] documents =
        {
            ("README.md", "src/CodeBrix.WebGpu.Skia/README.md"),
            ("AGENT-README.txt", "src/CodeBrix.WebGpu.Skia/AGENT-README.txt"),
            ("THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.txt"),
            ("LICENSE", "LICENSE"),
            ("icon-codebrix-128.png", "icon-codebrix-128.png"),
        };

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] different = documents.Where(document =>
        {
            using Stream stream = zip.GetEntry(document.Packed).Open();
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            return !copy.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, document.Source)));
        }).Select(document => document.Packed).ToArray();

        //Assert
        different.Should().BeEmpty();
    }

    [Fact]
    public void package_metadata_is_complete()
    {
        //Arrange
        string package = PackageUnderTest();

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        using Stream stream = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
        XDocument nuspec = XDocument.Load(stream);
        string Value(string name) => nuspec.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        XElement license = nuspec.Descendants().Single(element => element.Name.LocalName == "license");
        XElement repository = nuspec.Descendants().Single(element => element.Name.LocalName == "repository");

        //Assert
        Value("id").Should().Be("CodeBrix.WebGpu.Skia.MitLicenseForever");
        Regex.IsMatch(Value("version"), @"^1\.\d+\.\d{1,3}\.\d{1,4}$").Should().BeTrue();
        ((string)license.Attribute("type")).Should().Be("expression");
        license.Value.Should().Be("MIT");
        Value("icon").Should().Be("icon-codebrix-128.png");
        Value("readme").Should().Be("README.md");
        Value("projectUrl").Should().Be("https://github.com/ellisnet/CodeBrix.WebGpu");
        ((string)repository.Attribute("url")).Should().Be("https://github.com/ellisnet/CodeBrix.WebGpu");
        Value("authors").Should().Be("Jeremy Ellis");
        Value("description").Should().NotBeNullOrWhiteSpace();
    }
}
