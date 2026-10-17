using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>
/// The static capability status of one exported function, derived from its notes (the values of the
/// <c>codebrix_proc_status</c> enum of specs/codebrix.yml without the enum prefix, in the same order).
/// </summary>
public enum ProcStatus
{
    /// <summary>Outside the table (never assigned by the notes).</summary>
    Unknown = 0,

    /// <summary>Implemented; no recorded abort path (no note, or a <c>NOTE:</c> note).</summary>
    Implemented = 1,

    /// <summary>Not implemented by the engine: calling it ends the process (<c>ABORTS:</c>).</summary>
    AbortsUnimplemented = 2,

    /// <summary>Ends the process on the inputs the note names (<c>ABORTS</c> without a colon).</summary>
    AbortsOnError = 3,

    /// <summary>Always fails with this engine (<c>UNSUPPORTED:</c>).</summary>
    Unsupported = 4,

    /// <summary>Needs a backend the library lacks (decided by the native at run time, never by the notes).</summary>
    NotAvailableOnBackend = 5,
}

/// <summary>
/// The per-function notes of wgpu-native-tools/specs/proc-table-notes.txt (functions the engine does not
/// implement, that end the process on invalid input, or that always fail), rendered as comments on the
/// function-table entries and as the static status table behind <c>wgpuCodebrixGetProcStatus</c>.
/// </summary>
/// <remarks>
/// Format: one entry per line, <c>&lt;C symbol or pattern&gt; &lt;whitespace&gt; &lt;note&gt;</c>; <c>*</c> in the
/// symbol matches any run of characters; blank lines and lines starting with <c>#</c> are ignored. Every entry
/// must match at least one exported function; a function matched by several entries gets their notes joined.
/// Every note starts with its status keyword: <c>ABORTS:</c> (not implemented, ends the process),
/// <c>ABORTS </c> (ends the process on the inputs named), <c>UNSUPPORTED:</c> (always fails) or <c>NOTE:</c>
/// (implemented, with a caveat); a function matched by several entries takes the most severe.
/// </remarks>
public sealed class ProcNotes
{
    private readonly List<(string Pattern, Regex Matcher, string Note, ProcStatus Status, int Line)> entries = new List<(string, Regex, string, ProcStatus, int)>();

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
            string note = match.Groups[2].Value.Trim();
            ProcStatus status = Classify(note) ?? throw new SpecException(
                fileName + ":" + (index + 1),
                "the note must start with a status keyword: 'ABORTS:', 'ABORTS ', 'UNSUPPORTED:' or 'NOTE:'");
            notes.entries.Add((pattern, matcher, note, status, index + 1));
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

    /// <summary>The status a note's leading keyword gives, or null when it has none.</summary>
    /// <param name="note">The note text.</param>
    /// <returns>The status, or null.</returns>
    public static ProcStatus? Classify(string note)
    {
        if (note.StartsWith("ABORTS:", StringComparison.Ordinal)) return ProcStatus.AbortsUnimplemented;
        if (note.StartsWith("ABORTS ", StringComparison.Ordinal)) return ProcStatus.AbortsOnError;
        if (note.StartsWith("UNSUPPORTED:", StringComparison.Ordinal)) return ProcStatus.Unsupported;
        if (note.StartsWith("NOTE:", StringComparison.Ordinal)) return ProcStatus.Implemented;
        return null;
    }

    /// <summary>The static status of a symbol: the most severe status of every matching entry.</summary>
    /// <param name="symbol">The C symbol.</param>
    /// <returns>The status (<see cref="ProcStatus.Implemented" /> when no entry matches).</returns>
    public ProcStatus StatusFor(string symbol)
    {
        ProcStatus result = ProcStatus.Implemented;
        foreach ((string _, Regex matcher, string _, ProcStatus status, int _) in entries)
        {
            if (matcher.IsMatch(symbol) && Severity(status) > Severity(result)) result = status;
        }

        return result;
    }

    private static int Severity(ProcStatus status) => status switch
    {
        ProcStatus.AbortsUnimplemented => 3,
        ProcStatus.Unsupported => 2,
        ProcStatus.AbortsOnError => 1,
        _ => 0,
    };

    /// <summary>The note for a symbol, or null.</summary>
    /// <param name="symbol">The C symbol.</param>
    /// <returns>The joined notes of every matching entry, or null.</returns>
    public string For(string symbol)
    {
        List<string> found = entries.Where(e => e.Matcher.IsMatch(symbol)).Select(e => e.Note).ToList();
        return found.Count == 0 ? null : string.Join(" ", found);
    }
}
