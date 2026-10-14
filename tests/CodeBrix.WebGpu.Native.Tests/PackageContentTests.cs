using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>Opens the newest package of the build configuration the tests were built in and checks its contents.</summary>
public class PackageContentTests
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

    /// <summary>Every file packed under include/ and where it comes from in the repository.</summary>
    private static readonly (string Packed, string Source)[] IncludeFiles =
    {
        ("include/webgpu.h", "wgpu-native-tools/wgpu-native/ffi/webgpu-headers/webgpu.h"),
        ("include/wgpu.h", "wgpu-native-tools/wgpu-native/ffi/wgpu.h"),
        ("include/codebrix_wgpu.h", CodebrixHeaderCheck.HeaderPath),
        ("include/codebrix_wgpu_procs.h", ModuleRenderer.CHeaderPath),
        ("include/codebrix_wgpu_procs.rs", ModuleRenderer.RustPath),
        ("include/LICENSE-WgpuNative.txt", "wgpu-native-tools/licenses/LICENSE-WgpuNative.txt"),
    };

    [Fact]
    public void package_contains_the_native_module_headers_and_their_licence()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");
        List<string> problems = new List<string>();

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] packed = zip.Entries.Select(entry => entry.FullName)
            .Where(name => name.StartsWith("include/", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach ((string path, string source) in IncludeFiles)
        {
            ZipArchiveEntry entry = zip.GetEntry(path);
            if (entry == null)
            {
                problems.Add("missing " + path);
                continue;
            }

            using Stream stream = entry.Open();
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Root, source)))) problems.Add(path + " differs from " + source);
        }

        //Assert
        problems.Should().BeEmpty();
        packed.Should().Equal(IncludeFiles.Select(file => file.Packed).OrderBy(name => name, StringComparer.Ordinal));
        File.ReadAllText(Path.Combine(Root, "wgpu-native-tools/licenses/LICENSE-WgpuNative.txt")).Should().Contain("BSD 3-Clause License");
    }

    private static string[] CommittedNatives(string fileName)
    {
        string runtimes = Path.Combine(Root, "src", "CodeBrix.WebGpu.Native", "runtimes");
        return Directory.GetFiles(runtimes, fileName, SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void every_linux_and_android_native_has_the_soname_the_loader_reuses()
    {
        //Arrange
        string[] natives = CommittedNatives("libcodebrix_wgpu.so");

        //Act
        string[] sonames = natives.Select(path => NativeBinaryReader.ElfSoname(File.ReadAllBytes(path))).ToArray();

        //Assert
        natives.Select(path => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))))
            .Should().Equal("android-arm64", "android-x64", "linux-arm64", "linux-riscv64", "linux-x64");
        sonames.Should().AllBe("libcodebrix_wgpu.so");
    }

    [Fact]
    public void both_macos_natives_have_the_rpath_install_name()
    {
        //Arrange
        string[] natives = CommittedNatives("libcodebrix_wgpu.dylib");

        //Act
        string[] names = natives.SelectMany(path => NativeBinaryReader.MachOInstallNames(File.ReadAllBytes(path))).ToArray();

        //Assert
        natives.Select(path => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))).Should().Equal("osx-arm64", "osx-x64");
        names.Should().HaveCount(2);
        names.Should().AllBe("@rpath/libcodebrix_wgpu.dylib");
    }

    [Fact]
    public void the_binary_readers_refuse_other_formats()
    {
        //Arrange
        byte[] dll = File.ReadAllBytes(CommittedNatives("codebrix_wgpu.dll")[0]);

        //Act
        Action elf = () => NativeBinaryReader.ElfSoname(dll);
        Action macho = () => NativeBinaryReader.MachOInstallNames(dll);

        //Assert
        elf.Should().Throw<FormatException>();
        macho.Should().Throw<FormatException>();
    }

    [Fact]
    public void package_contains_nothing_but_the_assembly_the_documents_the_natives_and_the_headers()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");
        string[] expected =
        {
            "_rels/.rels", "[Content_Types].xml", "CodeBrix.WebGpu.Native.MitLicenseForever.nuspec",
            "lib/net10.0/CodeBrix.WebGpu.Native.dll", "lib/net10.0/CodeBrix.WebGpu.Native.xml",
            "README.md", "AGENT-README.txt", "THIRD-PARTY-NOTICES.txt", "LICENSE", "icon-codebrix-128.png",
        };

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] unexpected = zip.Entries.Select(entry => entry.FullName)
            .Where(name => !expected.Contains(name, StringComparer.Ordinal)
                && !name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
                && !name.StartsWith("runtimes/", StringComparison.Ordinal)
                && !name.StartsWith("include/", StringComparison.Ordinal))
            .ToArray();

        //Assert
        unexpected.Should().BeEmpty();
    }

    [Fact]
    public void package_documents_are_the_packages_own_files()
    {
        //Arrange
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");
        string root = Root;
        (string Packed, string Source)[] documents =
        {
            ("README.md", "README.md"),
            ("AGENT-README.txt", "AGENT-README.txt"),
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
        string package = NewestPackage();
        Assert.SkipWhen(package == null, "No " + Configuration + " package has been built yet.");

        //Act
        using ZipArchive zip = ZipFile.OpenRead(package);
        using Stream stream = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
        XDocument nuspec = XDocument.Load(stream);
        string Value(string name) => nuspec.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        XElement license = nuspec.Descendants().Single(element => element.Name.LocalName == "license");
        XElement repository = nuspec.Descendants().Single(element => element.Name.LocalName == "repository");

        //Assert
        Value("id").Should().Be("CodeBrix.WebGpu.Native.MitLicenseForever");
        Regex.IsMatch(Value("version"), @"^1\.\d+\.\d{1,3}\.\d{1,4}$").Should().BeTrue();
        ((string)license.Attribute("type")).Should().Be("expression");
        license.Value.Should().Be("MIT");
        Value("icon").Should().Be("icon-codebrix-128.png");
        Value("readme").Should().Be("README.md");
        Value("projectUrl").Should().Be("https://github.com/ellisnet/CodeBrix.WebGpu");
        ((string)repository.Attribute("url")).Should().Be("https://github.com/ellisnet/CodeBrix.WebGpu");
        Value("authors").Should().Be("Jeremy Ellis");
        Value("description").Should().NotBeNullOrWhiteSpace();
        nuspec.Descendants().Where(element => element.Name.LocalName == "dependency").Should().BeEmpty();
    }
}
