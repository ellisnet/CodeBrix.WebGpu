using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator;

/// <summary>Command line: <c>--check</c> (exit 1 on drift) or <c>--write</c>; optional <c>--repo &lt;path&gt;</c>.</summary>
public static class Program
{
    /// <summary>Runs the generator.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>0 on success; 1 on drift (--check); 2 on invalid input or usage.</returns>
    public static int Main(string[] args)
    {
        string mode = null;
        string repository = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--check":
                case "--write":
                    mode = args[index];
                    break;
                case "--repo" when index + 1 < args.Length:
                    repository = args[++index];
                    break;
                default:
                    return Usage("unknown argument '" + args[index] + "'");
            }
        }

        if (mode == null) return Usage("choose --check or --write");
        repository ??= BindingGenerator.FindRepositoryRoot(Directory.GetCurrentDirectory())
                       ?? BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory);
        if (repository == null) return Usage("cannot find CodeBrix.WebGpu.slnx above the current directory; pass --repo");

        SortedDictionary<string, string> outputs;
        try
        {
            outputs = BindingGenerator.Generate(repository);
        }
        catch (SpecException exception)
        {
            Console.Error.WriteLine("INVALID INPUT: " + exception.Message);
            return 2;
        }

        if (mode == "--write")
        {
            List<string> written = BindingGenerator.Write(repository, outputs);
            Console.WriteLine(written.Count == 0 ? "All " + outputs.Count + " outputs already current." : "Wrote " + written.Count + " of " + outputs.Count + " outputs:");
            foreach (string path in written) Console.WriteLine("  " + path);
            List<string> stale = BindingGenerator.Compare(repository, outputs);
            foreach (string difference in stale) Console.WriteLine(difference);
            return stale.Count == 0 ? 0 : 1;
        }

        List<string> differences = BindingGenerator.Compare(repository, outputs);
        if (differences.Count == 0)
        {
            Console.WriteLine("Generated files are current (" + outputs.Count + " outputs).");
            return 0;
        }

        Console.WriteLine("Generated files are OUT OF DATE - run with --write:");
        foreach (string difference in differences) Console.WriteLine(difference);
        return 1;
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine("error: " + problem);
        Console.Error.WriteLine("usage: CodeBrix.WebGpu.BindingGenerator (--check | --write) [--repo <repository root>]");
        return 2;
    }
}
