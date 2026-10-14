using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Model;

namespace CodeBrix.WebGpu.BindingGenerator.Rendering;

/// <summary>
/// Checks the hand-written C header wgpu-native-tools/codebrix-wgpu/include/codebrix_wgpu.h against
/// specs/codebrix.yml: the API-version define, every enum (entries and values, in order), every struct (member
/// declarations, in order), every function declaration and its <c>WGPUProc</c> typedef - and that the header
/// declares nothing the specification does not. The header ships in the package for native modules, so it must
/// be complete and current. Comparison ignores whitespace and <c>WGPU_NULLABLE</c>.
/// </summary>
public static class CodebrixHeaderCheck
{
    /// <summary>The header, relative to the repository root.</summary>
    public const string HeaderPath = "wgpu-native-tools/codebrix-wgpu/include/codebrix_wgpu.h";

    private static readonly Regex Comments = new Regex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline);

    /// <summary>Lists every difference between the header and the codebrix specification.</summary>
    /// <param name="model">The model.</param>
    /// <param name="header">The header text.</param>
    /// <returns>The problems; empty when the header is current.</returns>
    public static List<string> Check(ApiModel model, string header)
    {
        List<string> problems = new List<string>();
        string text = Normalize(Comments.Replace(header, " "));

        foreach (ApiConstant constant in model.Constants.Where(c => c.CName.StartsWith("WGPU_CODEBRIX_", StringComparison.Ordinal)))
        {
            string expected = "#define " + constant.CName + " (" + constant.CsValue.TrimEnd('u', 'l') + ")";
            if (!text.Contains(Normalize(expected), StringComparison.Ordinal)) problems.Add("missing or different: " + expected);
        }

        HashSet<string> enumNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiEnum value in model.Enums.Where(e => e.Values.Count > 0 && e.Values.All(v => v.SpecName == "codebrix")))
        {
            enumNames.Add(value.CName);
            string body = Body(text, "typedef enum " + value.CName + " {");
            if (body == null)
            {
                problems.Add("missing enum " + value.CName);
                continue;
            }

            List<string> expected = value.Values.Select(v => v.CName + " = 0x" + v.Value.ToString("X8", CultureInfo.InvariantCulture))
                .Append(value.CName + "_Force32 = 0x7FFFFFFF").ToList();
            List<string> actual = body.Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
            if (!actual.SequenceEqual(expected)) problems.Add("enum " + value.CName + ": header [" + string.Join(", ", actual) + "] expected [" + string.Join(", ", expected) + "]");
        }

        foreach (Match match in Regex.Matches(text, @"typedef enum (\w+) \{"))
        {
            if (!enumNames.Contains(match.Groups[1].Value)) problems.Add("enum not in specs/codebrix.yml: " + match.Groups[1].Value);
        }

        HashSet<string> structNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiStruct value in model.Structs.Where(s => s.SpecName == "codebrix"))
        {
            structNames.Add(value.CName);
            string body = Body(text, "typedef struct " + value.CName + " {");
            if (body == null)
            {
                problems.Add("missing struct " + value.CName);
                continue;
            }

            List<string> expected = value.Fields.Select(f => Normalize(f.Type.C + " " + f.CName)).ToList();
            List<string> actual = body.Split(';').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            if (!actual.SequenceEqual(expected)) problems.Add("struct " + value.CName + ": header [" + string.Join("; ", actual) + "] expected [" + string.Join("; ", expected) + "]");
        }

        foreach (Match match in Regex.Matches(text, @"typedef struct (\w+) \{"))
        {
            if (!structNames.Contains(match.Groups[1].Value)) problems.Add("struct not in specs/codebrix.yml: " + match.Groups[1].Value);
        }

        HashSet<string> functionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiFunction function in model.Functions.Where(f => f.SpecName == "codebrix"))
        {
            functionNames.Add(function.CName);
            string returns = function.Return?.C ?? "void";
            string parameters = ModuleRenderer.CParameters(function);
            string declaration = "WGPU_EXPORT " + returns + " " + function.CName + "(" + parameters + ") WGPU_FUNCTION_ATTRIBUTE;";
            string proc = "typedef " + returns + " (*WGPUProc" + function.CName.Substring(4) + ")(" + parameters + ") WGPU_FUNCTION_ATTRIBUTE;";
            if (!text.Contains(Normalize(declaration), StringComparison.Ordinal)) problems.Add("missing or different: " + declaration);
            if (!text.Contains(Normalize(proc), StringComparison.Ordinal)) problems.Add("missing or different: " + proc);
        }

        foreach (Match match in Regex.Matches(text, @"WGPU_EXPORT [^;]*?\b(wgpu\w+) ?\("))
        {
            if (!functionNames.Contains(match.Groups[1].Value)) problems.Add("function not in specs/codebrix.yml: " + match.Groups[1].Value);
        }

        return problems;
    }

    private static string Body(string text, string start)
    {
        int open = text.IndexOf(Normalize(start), StringComparison.Ordinal);
        if (open < 0) return null;
        open = text.IndexOf('{', open);
        int close = text.IndexOf('}', open);
        return close < 0 ? null : text.Substring(open + 1, close - open - 1);
    }

    /// <summary>Collapses whitespace (also around punctuation) and drops <c>WGPU_NULLABLE</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        string result = Regex.Replace(text.Replace("WGPU_NULLABLE", " "), @"\s+", " ");
        result = Regex.Replace(result, @" ?([(){},;*]) ?", "$1");
        result = Regex.Replace(result, @"([(){},;*])", " $1 ");
        return Regex.Replace(result, @"\s+", " ").Trim();
    }
}
