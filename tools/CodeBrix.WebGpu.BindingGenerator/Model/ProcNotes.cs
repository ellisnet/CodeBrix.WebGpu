using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>
/// The per-function notes of wgpu-native-tools/specs/proc-table-notes.txt (functions the engine does not
/// implement, or that end the process on invalid input), rendered as comments on the function-table entries.
/// </summary>
/// <remarks>
/// Format: one entry per line, <c>&lt;C symbol or pattern&gt; &lt;whitespace&gt; &lt;note&gt;</c>; <c>*</c> in the
/// symbol matches any run of characters; blank lines and lines starting with <c>#</c> are ignored. Every entry
/// must match at least one exported function; a function matched by several entries gets their notes joined.
/// </remarks>
public sealed class ProcNotes
{
    private readonly List<(string Pattern, Regex Matcher, string Note, int Line)> entries = new List<(string, Regex, string, int)>();

    /// <summary>No notes.</summary>
    public static ProcNotes Empty => new ProcNotes();

    /// <summary>Parses the notes file.</summary>
    /// <param name="text">The file text.</param>
    /// <param name="fileName">The file name, for messages.</param>
    /// <returns>The notes.</returns>
    /// <exception cref="SpecException">A line has no note.</exception>
    public static ProcNotes Parse(string text, string fileName)
    {
        ProcNotes notes = new ProcNotes();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Match match = Regex.Match(line, @"^(\S+)\s+(.+)$");
            if (!match.Success) throw new SpecException(fileName + ":" + (index + 1), "expected '<symbol> <note>'");
            string pattern = match.Groups[1].Value;
            Regex matcher = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);
            notes.entries.Add((pattern, matcher, match.Groups[2].Value.Trim(), index + 1));
        }

        return notes;
    }

    /// <summary>Checks that every entry names at least one function.</summary>
    /// <param name="symbols">Every exported C symbol.</param>
    /// <param name="fileName">The file name, for messages.</param>
    /// <exception cref="SpecException">An entry matches nothing.</exception>
    public void Validate(IEnumerable<string> symbols, string fileName)
    {
        List<string> all = symbols.ToList();
        List<string> unmatched = entries.Where(e => !all.Any(s => e.Matcher.IsMatch(s))).Select(e => "line " + e.Line + " '" + e.Pattern + "'").ToList();
        if (unmatched.Count > 0) throw new SpecException(fileName, "entries that match no exported function: " + string.Join(", ", unmatched));
    }

    /// <summary>The note for a symbol, or null.</summary>
    /// <param name="symbol">The C symbol.</param>
    /// <returns>The joined notes of every matching entry, or null.</returns>
    public string For(string symbol)
    {
        List<string> found = entries.Where(e => e.Matcher.IsMatch(symbol)).Select(e => e.Note).ToList();
        return found.Count == 0 ? null : string.Join(" ", found);
    }
}
