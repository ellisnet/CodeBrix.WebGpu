using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Opens the newest Graphics package of the build configuration the tests were built in and checks its contents and dependencies.</summary>
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

    private static string NewestPackage()
    {
        string root = RepositoryRoot();
        string folder = root == null ? null : Path.Combine(root, "src", "CodeBrix.WebGpu.Graphics", "bin", Configuration);
        return folder != null && Directory.Exists(folder)
            ? Directory.GetFiles(folder, "CodeBrix.WebGpu.Graphics.MitLicenseForever.*.nupkg").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault()
            : null;
    }

    [Fact]
    public void package_contains_the_assembly_and_its_own_documents()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " Graphics package has been built yet.");

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] entries = zip.Entries.Select(entry => entry.FullName).ToArray();
        using StreamReader readme = new StreamReader(zip.GetEntry("README.md").Open());
        string readmeText = readme.ReadToEnd();

        //Assert
        entries.Should().Contain(new[]
        {
            "lib/net10.0/CodeBrix.WebGpu.Graphics.dll",
            "lib/net10.0/CodeBrix.WebGpu.Graphics.xml",
            "README.md",
            "AGENT-README.txt",
            "THIRD-PARTY-NOTICES.txt",
            "LICENSE",
            "icon-codebrix-128.png",
        });
        entries.Should().NotContain(name => name.StartsWith("runtimes/", StringComparison.Ordinal));
        readmeText.Should().StartWith("# CodeBrix.WebGpu.Graphics");
    }

    [Fact]
    public void package_depends_on_the_native_package_only()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " Graphics package has been built yet.");

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        ZipArchiveEntry nuspecEntry = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(stream);
        var dependencies = nuspec.Descendants().Where(element => element.Name.LocalName == "dependency")
            .Select(element => new { Id = (string)element.Attribute("id"), Version = (string)element.Attribute("version") })
            .ToArray();

        //Assert
        dependencies.Select(dependency => dependency.Id).Should().BeEquivalentTo(new[] { "CodeBrix.WebGpu.Native.MitLicenseForever" });
        dependencies.Single().Version.Should().StartWith("1.");
    }
}
