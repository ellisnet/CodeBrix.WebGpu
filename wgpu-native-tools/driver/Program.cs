// =================================================================================================
// WgpuNativeDriver - wgpu-native-tools' dog-food driver for CodeBrix.RustTools(.Docker)
// =================================================================================================
//
//   dotnet run --project driver/WgpuNativeDriver.csproj -- <command> [options]
//
// Commands (each prints what it does; exit 0 = success):
//   license-audit --project <crate dir> --triples <t1,t2,...> --out <file.tsv>
//       RustWorkbench.AuditLicensesAsync (cargo metadata --filter-platform <t>, LicensePolicy
//       .Permissive()) once per triple, then the union of the crates that LINK INTO the library
//       on any of those triples (normal edges from the root; build-dependencies and proc-macros
//       never link) is written as TSV for licenses/compose-license-wgpunative.sh. Exit 3 when a
//       linked crate is Rejected or Unresolved by the policy (STOP - never ship it).
//   android-build --project <crate dir> --ndk <ndk dir> --api <level> --triple <t> --out <dir>
//                 [--env KEY=VALUE ...]
//       RustWorkbench.BuildAndroidAsync with AndroidLinkProfile (the NDK's clang as linker and C
//       compiler, llvm-ar), release, --locked; the .so lands in <out>/<triple>/.
//   linux-build  --stage <dir with wgpu-native/ + codebrix-wgpu/> --image <name:tag> --target <x64|arm64|riscv64>
//                --out <dir> --endpoint <unix://...> [--label-prefix p] [--cache-volume v]
//                [--container-prefix c] [--env KEY=VALUE ...]
//       CodeBrix.RustTools.Docker: engine check, image (an existing local image, no layers),
//       container (labelled; cargo cache volume), source copy, `cargo fetch` (network), licence
//       audit in the container, `cargo build --release --locked --offline`, artifact retrieval.
//   cleanup --endpoint <unix://...> [--label-prefix p] [--cache-volume v] [--container-prefix c] [--volumes]
//       CleanupAsync: every container (and with --volumes the cache/target volumes) carrying the
//       label prefix. Derived images are built by linux/build.sh (podman build) and never removed here.
//
// It is NOT part of CodeBrix.WebGpu.slnx and is never packed. Its only dependencies are
// CodeBrix.RustTools.MitLicenseForever and CodeBrix.RustTools.Docker.MitLicenseForever (nuget.org).
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
// =================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeBrix.Docker;
using CodeBrix.RustTools;
using CodeBrix.RustTools.Building;
using CodeBrix.RustTools.Docker;
using CodeBrix.RustTools.Docker.Artifacts;
using CodeBrix.RustTools.Docker.Containers;
using CodeBrix.RustTools.Docker.Images;
using CodeBrix.RustTools.Licensing;
using CodeBrix.RustTools.Projects;
using CodeBrix.RustTools.Toolchain;

namespace WgpuNativeDriver
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: WgpuNativeDriver <license-audit|android-build|linux-build|cleanup> [options] (see Program.cs)");
                return 2;
            }

            var options = ParseOptions(args.Skip(1).ToArray());
            try
            {
                switch (args[0])
                {
                    case "license-audit": return await LicenseAuditAsync(options);
                    case "android-build": return await AndroidBuildAsync(options);
                    case "linux-build": return await LinuxBuildAsync(options);
                    case "cleanup": return await CleanupAsync(options);
                    default:
                        Console.Error.WriteLine($"unknown command {args[0]}");
                        return 2;
                }
            }
            catch (ToolchainNotFoundException ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                foreach (var g in ex.Guidance) { Console.Error.WriteLine(g); }
                return 1;
            }
            catch (DockerUnavailableException ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                foreach (var g in ex.Guidance) { Console.Error.WriteLine(g); }
                return 1;
            }
            catch (RustToolsException ex)
            {
                Console.Error.WriteLine($"ERROR ({ex.GetType().Name}): {ex.Message}");
                if (ex is ContainerBuildException cbe)
                {
                    Console.Error.WriteLine(cbe.StandardOutput);
                    Console.Error.WriteLine(cbe.StandardError);
                }
                return 1;
            }
        }

        // --options value [--options value ...]; repeated --env collect into a list.
        private static Dictionary<string, List<string>> ParseOptions(string[] args)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { throw new ArgumentException($"unexpected argument {args[i]}"); }
                var key = args[i].Substring(2);
                var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) ? args[++i] : "true";
                if (!result.TryGetValue(key, out var list)) { result[key] = list = new List<string>(); }
                list.Add(value);
            }
            return result;
        }

        private static string Get(Dictionary<string, List<string>> o, string key, string fallback = null)
        {
            if (o.TryGetValue(key, out var v)) { return v[v.Count - 1]; }
            if (fallback != null) { return fallback; }
            throw new ArgumentException($"--{key} is required");
        }

        private static Dictionary<string, string> EnvOf(Dictionary<string, List<string>> o)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (o.TryGetValue("env", out var list))
            {
                foreach (var kv in list)
                {
                    var eq = kv.IndexOf('=');
                    env[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
            }
            return env;
        }

        private static void Log(string message) => Console.WriteLine($"[driver {DateTime.Now:HH:mm:ss}] {message}");

        // ---------------------------------------------------------------------------------------
        // license-audit
        // ---------------------------------------------------------------------------------------
        private static async Task<int> LicenseAuditAsync(Dictionary<string, List<string>> o)
        {
            var projectDir = Path.GetFullPath(Get(o, "project"));
            var triples = Get(o, "triples").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var outPath = Path.GetFullPath(Get(o, "out"));
            var workbench = new RustWorkbench();
            var project = workbench.OpenProject(projectDir);
            Log($"project {project.Name} at {project.Directory}; policy LicensePolicy.Permissive(); {triples.Length} target(s)");

            var linked = new SortedDictionary<string, (CargoMetadataPackage Package, CrateLicenseFinding Finding, SortedSet<string> Triples)>(StringComparer.Ordinal);
            var blocking = 0;
            foreach (var triple in triples)
            {
                var metadataOptions = new CargoMetadataOptions { FilterPlatform = triple, Locked = true };
                var report = await workbench.AuditLicensesAsync(project, LicensePolicy.Permissive(), metadataOptions);
                var metadata = await project.GetMetadataAsync(metadataOptions);
                var closure = LinkedClosure(metadata);
                Log($"{triple}: audit {(report.Passed ? "PASSED" : "FAILED")} - {report.ApprovedCount} approved, {report.RejectedCount} rejected, " +
                    $"{report.UnresolvedCount} unresolved, {report.ExemptCount} exempt; {closure.Count} crates link into the library");
                foreach (var w in report.Warnings) { Log($"  warning: {w}"); }
                foreach (var id in closure)
                {
                    var package = metadata.GetPackage(id);
                    if (package.IsLocal) { continue; }   // wgpu-native (vendored, its own licence) and codebrix-wgpu (ours)
                    var finding = report.GetFinding(package.Name);
                    var key = $"{package.Name}\t{package.Version}";
                    if (!linked.TryGetValue(key, out var entry))
                    {
                        entry = (package, finding, new SortedSet<string>(StringComparer.Ordinal));
                        linked[key] = entry;
                    }
                    entry.Triples.Add(triple);
                }
                foreach (var f in report.BlockingFindings)
                {
                    var p = metadata.FindPackages(f.CrateName).FirstOrDefault();
                    if (p != null && closure.Contains(p.Id))
                    {
                        blocking++;
                        Log($"  BLOCKING ({triple}): {f.CrateName} {f.Version} '{f.DeclaredExpression}' - {f.Status}: {f.Reason}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("# name\tversion\tlicense\tlicense_file\tmanifest_dir\trepository\tstatus\ttriples\n");
            foreach (var (_, (package, finding, ts)) in linked)
            {
                sb.Append(package.Name).Append('\t').Append(package.Version).Append('\t').Append(package.License ?? "").Append('\t')
                  .Append(package.LicenseFilePath ?? "").Append('\t').Append(package.ManifestDirectory).Append('\t')
                  .Append(package.Repository ?? "").Append('\t').Append(finding?.Status.ToString() ?? "NotAudited").Append('\t')
                  .Append(string.Join(',', ts)).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString());
            Log($"{linked.Count} distinct crates written to {outPath}");
            if (blocking > 0)
            {
                Log($"STOP: {blocking} linked crate(s) are not permissively licensed or have an unresolved licence");
                return 3;
            }
            return 0;
        }

        // The packages compiled INTO the library: the root plus everything reachable over NORMAL
        // dependency edges; proc-macro crates run in the compiler only and are excluded.
        private static HashSet<string> LinkedClosure(CargoMetadata metadata)
        {
            var root = metadata.ResolveRoot ?? metadata.WorkspaceMembers[0];
            var seen = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var node = metadata.GetNode(queue.Dequeue());
                if (node == null) { continue; }
                foreach (var dep in node.Deps)
                {
                    var isNormal = dep.DepKinds.Count == 0 || dep.HasKind(CargoDependencyKind.Normal);
                    if (!isNormal) { continue; }
                    var package = metadata.GetPackage(dep.PackageId);
                    if (package.Targets.Any(t => t.HasKind(CargoTargetKind.ProcMacro))) { continue; }
                    if (seen.Add(dep.PackageId)) { queue.Enqueue(dep.PackageId); }
                }
            }
            return seen;
        }

        // ---------------------------------------------------------------------------------------
        // android-build
        // ---------------------------------------------------------------------------------------
        private static async Task<int> AndroidBuildAsync(Dictionary<string, List<string>> o)
        {
            var projectDir = Path.GetFullPath(Get(o, "project"));
            var ndkDir = Path.GetFullPath(Get(o, "ndk"));
            var api = int.Parse(Get(o, "api"));
            var triple = Get(o, "triple");
            var outDir = Path.GetFullPath(Get(o, "out"));
            var workbench = new RustWorkbench(new RustWorkbenchOptions { AndroidNdkPath = ndkDir });
            var project = workbench.OpenProject(projectDir);
            var ndk = AndroidNdk.Load(ndkDir);
            var profile = AndroidLinkProfile.ForNdk(ndk, api);
            Log($"NDK {ndk.Version} at {ndk.Path}; AndroidLinkProfile API {profile.ApiLevel}; {triple}");
            foreach (var kv in profile.EnvironmentFor(triple)) { Log($"  profile env {kv.Key}={kv.Value}"); }

            var build = new CargoBuildOptions { Profile = CargoProfile.Release, Locked = true };
            foreach (var kv in EnvOf(o)) { build.Environment[kv.Key] = kv.Value; }
            var android = new AndroidBuildOptions { Profile = profile, ApiLevel = api, Layout = AndroidOutputLayout.ByTarget };
            android.Triples.Clear();
            android.Triples.Add(triple);
            var progress = new Progress<CargoProgress>(p => { if (p.Phase == CargoProgressPhase.Warning) { Log($"  {p}"); } });
            var result = await workbench.BuildAndroidAsync(project, outDir, build, android, progress);
            foreach (var (t, r) in result.Builds)
            {
                Log($"{t}: {(r.Success ? "built" : "FAILED")} in {r.Duration.TotalSeconds:F0}s, exit {r.ExitCode}, {r.WarningCount} warnings");
                Log($"  command: {r.CommandLine}");
                foreach (var e in r.Errors) { Console.Error.WriteLine(e.Rendered); }
                if (!r.Success) { Console.Error.WriteLine(r.StandardError); }
            }
            foreach (var f in result.Files) { Log($"  file {f.Path} ({f.Abi})"); }
            return result.Success ? 0 : 1;
        }

        // ---------------------------------------------------------------------------------------
        // linux-build
        // ---------------------------------------------------------------------------------------
        private static DockerRustWorkbenchOptions DockerOptions(Dictionary<string, List<string>> o) => new DockerRustWorkbenchOptions
        {
            Endpoint = Get(o, "endpoint"),
            LabelPrefix = Get(o, "label-prefix", "codebrix.webgpu.natives"),
            CacheVolumeName = Get(o, "cache-volume", "codebrix-wgpu-cargo-cache"),
            ContainerNamePrefix = Get(o, "container-prefix", "codebrix-wgpu-natives"),
        };

        private static async Task<int> LinuxBuildAsync(Dictionary<string, List<string>> o)
        {
            var stage = Path.GetFullPath(Get(o, "stage"));
            var image = Get(o, "image");
            var outDir = Path.GetFullPath(Get(o, "out"));
            var target = Get(o, "target") switch
            {
                "x64" => CrossTarget.LinuxX64,
                "arm64" => CrossTarget.LinuxArm64,
                "riscv64" => CrossTarget.LinuxRiscV64,
                var t => throw new ArgumentException($"unknown --target {t}"),
            };
            var colon = image.LastIndexOf(':');
            using var workbench = new DockerRustWorkbench(DockerOptions(o));
            var engine = await workbench.CheckEngineAsync();
            Log($"engine: {engine.Endpoint} {engine.ServerVersion} ({engine.OperatingSystem}, {engine.Architecture}) available={engine.IsAvailable}");
            if (!engine.IsAvailable)
            {
                foreach (var g in workbench.GetDockerGuidance(engine)) { Console.Error.WriteLine(g); }
                return 1;
            }
            var rustImage = await workbench.EnsureImageAsync(new BuildImageSpec { BaseImage = image.Substring(0, colon), Tag = image.Substring(colon + 1) },
                new Progress<string>(l => Log($"  image: {l}")));
            Log($"image {rustImage.Reference} (id {rustImage.ImageId}, base image, reused={rustImage.WasReused})");

            Directory.CreateDirectory(outDir);
            var containerOptions = new BuildContainerOptions();
            // /out: where the built library is copied with ExecAsync (see the FIXLIST note below).
            containerOptions.Mounts.Add(MountSpec.Bind(outDir, "/out", readOnly: false));
            foreach (var kv in EnvOf(o)) { containerOptions.Env[kv.Key] = kv.Value; }
            await using var container = await workbench.StartContainerAsync(rustImage, containerOptions);
            Log($"container {container.Name}; cargo cache volume {container.CacheVolumeName}; target volume {container.TargetVolumeName}");
            var copy = await workbench.CopySourceAsync(container, stage);
            Log($"copied {copy.FileCount} files ({copy.TotalBytes} bytes) into {copy.ContainerDirectory} in {copy.Duration.TotalSeconds:F1}s");

            var env = EnvOf(o);
            var fetchOptions = new CargoBuildOptions { WorkingDirectory = "codebrix-wgpu", Locked = true };
            foreach (var kv in env) { fetchOptions.Environment[kv.Key] = kv.Value; }
            var fetch = await workbench.RunCargoAsync(container, CargoCommand.Fetch, fetchOptions, target);
            Log($"cargo fetch: {(fetch.Success ? "ok" : "FAILED")} in {fetch.Duration.TotalSeconds:F0}s ({fetch.CommandLine})");
            if (!fetch.Success) { Console.Error.WriteLine(fetch.StandardError); return 1; }

            var buildOptions = new CargoBuildOptions
            {
                WorkingDirectory = "codebrix-wgpu",
                Profile = CargoProfile.Release,
                Locked = true,
                Offline = true,
                LicensePolicy = LicensePolicy.Permissive(),
                LicenseGateMode = LicenseGateMode.ReportOnly,
                ExpectedCrateCount = 0,
                KeepRawOutput = true,
            };
            foreach (var kv in env) { buildOptions.Environment[kv.Key] = kv.Value; }
            var progress = new Progress<CargoProgress>(p => { if (p.Phase == CargoProgressPhase.Warning) { Log($"  {p}"); } });
            var build = await workbench.RunCargoAsync(container, CargoCommand.Build, buildOptions, target, progress);
            Log($"cargo build: {(build.Success ? "ok" : "FAILED")} in {build.Duration.TotalSeconds:F0}s, {build.WarningCount} warnings ({build.CommandLine})");
            if (build.LicenseAudit != null)
            {
                Log($"in-container licence audit: {(build.LicenseAudit.Passed ? "PASSED" : "FAILED")} - {build.LicenseAudit.ApprovedCount} approved, " +
                    $"{build.LicenseAudit.RejectedCount} rejected, {build.LicenseAudit.UnresolvedCount} unresolved");
            }
            if (!build.Success)
            {
                foreach (var e in build.Errors) { Console.Error.WriteLine(e.Rendered); }
                Console.Error.WriteLine(build.StandardError);
                return 1;
            }
            // RetrieveArtifactsAsync needs the artifacts cargo reported; with podman 5.4.2's API the
            // result carries only a few of cargo's JSON messages (FIXLIST, 2026-10-11), so the
            // library is copied out with ExecAsync into the /out bind mount instead.
            Log($"{build.Artifacts.Count} artifact(s) / {build.RawMessages?.Count ?? 0} JSON messages reported by RunCargoAsync");
            var triple = CrossTargetInfo.Get(target).Triple;
            var hostTriple = engine.NativeLinuxTriple;
            var builtPath = triple == hostTriple ? "/work/target/release/libcodebrix_wgpu.so" : $"/work/target/{triple}/release/libcodebrix_wgpu.so";
            var copied = await workbench.ExecAsync(container, new[] { "sh", "-c", $"cp {builtPath} /out/libcodebrix_wgpu.so && sha256sum /out/libcodebrix_wgpu.so && rustc --version" });
            Log($"copied {builtPath} -> {outDir}/libcodebrix_wgpu.so: {copied.Trim().Replace('\n', ' ')}");
            return File.Exists(Path.Combine(outDir, "libcodebrix_wgpu.so")) ? 0 : 1;
        }

        private static async Task<int> CleanupAsync(Dictionary<string, List<string>> o)
        {
            using var workbench = new DockerRustWorkbench(DockerOptions(o));
            var result = await workbench.CleanupAsync(new CleanupOptions { RemoveContainers = true, RemoveVolumes = o.ContainsKey("volumes"), RemoveImages = false });
            Log($"cleanup: {result.RemovedContainers.Count} containers, {result.RemovedVolumes.Count} volumes removed; {result.Failures.Count} failures");
            foreach (var f in result.Failures) { Log($"  {f}"); }
            return result.Succeeded ? 0 : 1;
        }
    }
}
