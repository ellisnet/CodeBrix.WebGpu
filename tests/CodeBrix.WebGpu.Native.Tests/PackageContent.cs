using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>Opens the newest package of the build configuration the tests were built in and checks its contents.</summary>
public class PackageContent
{
    private static readonly string Root =
        BindingGenerator.BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Cannot find CodeBrix.WebGpu.slnx");

    private static string Configuration =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    private static string NewestPackage()
    {
        string folder = Path.Combine(Root, "src", "CodeBrix.WebGpu.Native", "bin", Configuration);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "CodeBrix.WebGpu.Native.MitLicenseForever.*.nupkg").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault()
            : null;
    }

    [Fact]
    public void package_contains_the_assembly_and_the_documents()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] entries = zip.Entries.Select(entry => entry.FullName).ToArray();

        //Assert
        entries.Should().Contain(new[]
        {
            "lib/net10.0/CodeBrix.WebGpu.Native.dll",
            "lib/net10.0/CodeBrix.WebGpu.Native.xml",
            "README.md",
            "AGENT-README.txt",
            "THIRD-PARTY-NOTICES.txt",
            "LICENSE",
            "icon-codebrix-128.png",
        });
    }

    [Fact]
    public void package_contains_every_committed_native_and_nothing_else_under_runtimes()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");
        string runtimes = Path.Combine(Root, "src", "CodeBrix.WebGpu.Native", "runtimes");
        string[] committed = Directory.Exists(runtimes)
            ? Directory.GetFiles(runtimes, "*", SearchOption.AllDirectories)
                .Select(path => "runtimes/" + Path.GetRelativePath(runtimes, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] packed = zip.Entries.Select(entry => entry.FullName)
            .Where(name => name.StartsWith("runtimes/", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        //Assert
        packed.Should().Equal(committed);
        packed.Where(name => !name.Contains("/native/", StringComparison.Ordinal)).Should().BeEmpty();
    }
}
