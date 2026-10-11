using System;

namespace CodeBrix.WebGpu.BindingGenerator.Yaml;

/// <summary>A specification file is invalid; the message names the file and the path of the node.</summary>
public sealed class SpecException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="path">The path of the offending node (<c>$.structs[2].members[0]</c>).</param>
    /// <param name="message">What is wrong with it.</param>
    public SpecException(string path, string message)
        : base(path + ": " + message)
    {
        NodePath = path;
    }

    /// <summary>The path of the offending node.</summary>
    public string NodePath { get; }
}
