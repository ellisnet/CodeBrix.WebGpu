using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// OPT-IN corpus smoke test: loads every .glb / .gltf below the folders named by the environment variable
/// CODEBRIX_WEBGPU_MODEL_CORPUS (separated by the path-list separator, ':' on Linux) and checks that each loads or
/// fails with an <see cref="InvalidDataException" /> carrying a reason. One texture cache per pack (first folder below a
/// corpus root). Prints load times per pack; CODEBRIX_WEBGPU_MODEL_CORPUS_REPORT names an optional file for the summary.
/// Skipped by default (the corpora are large and not part of the repository).
/// </summary>
public class ModelCorpusTests : IClassFixture<GraphicsGpuFixture>
{
    private const string CorpusVariable = "CODEBRIX_WEBGPU_MODEL_CORPUS";
    private const string ReportVariable = "CODEBRIX_WEBGPU_MODEL_CORPUS_REPORT";
    private readonly GraphicsGpuFixture gpu;

    public ModelCorpusTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void every_corpus_model_loads_or_fails_with_a_reason()
    {
        //Arrange
        string roots = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(roots), "Opt-in: set " + CorpusVariable + " to one or more folders of glTF models (for example the Kenney 3D assets and a Poly Haven download) to run the corpus smoke test.");
        gpu.RequireDevice();
        StringBuilder report = new StringBuilder();
        Dictionary<string, (int Loaded, int Failed, double Milliseconds, double Slowest, string SlowestFile)> packs = new Dictionary<string, (int, int, double, double, string)>(StringComparer.Ordinal);
        Dictionary<string, int> warningKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, int> validationMessages = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> failures = new List<string>();
        int total = 0;

        //Act
        foreach (string root in roots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.True(Directory.Exists(root), "The corpus folder '" + root + "' does not exist.");
            IEnumerable<IGrouping<string, string>> byPack = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal)
                .GroupBy(file => Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "/" + Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[0]);
            foreach (IGrouping<string, string> pack in byPack)
            {
                using ModelTextureCache cache = new ModelTextureCache();
                ModelLoadOptions options = new ModelLoadOptions { TextureCache = cache };
                (int loaded, int failed, double milliseconds, double slowest, string slowestFile) = (0, 0, 0.0, 0.0, null);
                foreach (string file in pack)
                {
                    total++;
                    Stopwatch watch = Stopwatch.StartNew();
                    try
                    {
                        using Model model = gpu.Context.LoadModel(file, options);
                        watch.Stop();
                        loaded++;
                        foreach (string warning in model.Warnings)
                        {
                            string kind = Kind(warning);
                            warningKinds[kind] = warningKinds.GetValueOrDefault(kind) + 1;
                            if (kind == "failed glTF validation") validationMessages[warning] = validationMessages.GetValueOrDefault(warning) + 1;
                        }
                    }
                    catch (InvalidDataException exception)
                    {
                        watch.Stop();
                        failed++;
                        failures.Add(file + ": " + exception.Message);
                    }

                    double elapsed = watch.Elapsed.TotalMilliseconds;
                    milliseconds += elapsed;
                    if (elapsed > slowest)
                    {
                        slowest = elapsed;
                        slowestFile = Path.GetFileName(file);
                    }
                }

                packs[pack.Key] = (loaded, failed, milliseconds, slowest, slowestFile);
                gpu.Context.TakeErrors().Should().BeEmpty();
            }
        }

        //Assert (and report)
        report.AppendLine("glTF corpus: " + total + " files, " + packs.Values.Sum(p => p.Loaded) + " loaded, " + failures.Count + " failed with a reason.");
        report.AppendLine("pack | loaded | failed | total ms | mean ms | slowest ms (file)");
        foreach (KeyValuePair<string, (int Loaded, int Failed, double Milliseconds, double Slowest, string SlowestFile)> pack in packs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            int count = pack.Value.Loaded + pack.Value.Failed;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:0} | {4:0.0} | {5:0.0} ({6})", pack.Key, pack.Value.Loaded, pack.Value.Failed, pack.Value.Milliseconds, count == 0 ? 0 : pack.Value.Milliseconds / count, pack.Value.Slowest, pack.Value.SlowestFile));
        }

        report.AppendLine("warnings by kind:");
        foreach (KeyValuePair<string, int> kind in warningKinds.OrderByDescending(k => k.Value)) report.AppendLine("  " + kind.Value + " x " + kind.Key);
        report.AppendLine("validation messages of files read without validation:");
        foreach (KeyValuePair<string, int> message in validationMessages.OrderByDescending(k => k.Value)) report.AppendLine("  " + message.Value + " x " + message.Key);
        report.AppendLine("failures:");
        foreach (string failure in failures) report.AppendLine("  " + failure);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        string reportFile = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrWhiteSpace(reportFile)) File.WriteAllText(reportFile, report.ToString());
        total.Should().BeGreaterThan(0);
        foreach (string failure in failures) failure.Should().Contain("could not be read");
    }

    private static string Kind(string warning)
    {
        int uses = warning.IndexOf(" uses ", StringComparison.Ordinal);
        if (warning.StartsWith("Material", StringComparison.Ordinal) && uses > 0) return "material" + warning.Substring(uses);
        foreach (string marker in new[] { "failed glTF validation", "only triangles", "KHR_animation_pointer", "no tangents", "cannot decode", "could not be decoded", "TEXCOORD_", "no POSITION" })
        {
            if (warning.Contains(marker, StringComparison.Ordinal)) return marker;
        }

        return warning.Length > 80 ? warning.Substring(0, 80) : warning;
    }
}
