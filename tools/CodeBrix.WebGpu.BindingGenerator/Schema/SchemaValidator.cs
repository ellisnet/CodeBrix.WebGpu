using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Schema;

/// <summary>
/// Validates a <see cref="SpecNode" /> tree against a JSON Schema (draft-07 keywords as used by
/// webgpu-headers' schema.json: type, enum, pattern, minimum, maximum, properties, required,
/// additionalProperties, unevaluatedProperties, items, $ref, oneOf, anyOf, allOf).
/// </summary>
/// <remarks>
/// The first violation found throws a <see cref="SpecException" /> naming the path of the offending node.
/// For <c>oneOf</c> / <c>anyOf</c> failures, the reported reason is the one from the alternative that got
/// furthest into the node (deepest path), which is nearly always the alternative the author meant.
/// </remarks>
public sealed class SchemaValidator
{
    private readonly JsonElement root;

    /// <summary>Creates a validator over the text of a JSON schema.</summary>
    /// <param name="schemaJson">The schema text.</param>
    public SchemaValidator(string schemaJson)
    {
        root = JsonDocument.Parse(schemaJson).RootElement.Clone();
    }

    /// <summary>Validates a document.</summary>
    /// <param name="document">The root node.</param>
    /// <param name="sourceName">The file name, prefixed to the error path.</param>
    /// <exception cref="SpecException">The first violation.</exception>
    public void Validate(SpecNode document, string sourceName)
    {
        Failure failure = Check(root, document);
        if (failure != null) throw new SpecException(sourceName + " " + failure.Path, failure.Message);
    }

    private sealed class Failure
    {
        public Failure(string path, string message)
        {
            Path = path;
            Message = message;
        }

        public string Path { get; }

        public string Message { get; }

        public int Depth => Path.Split('.', '[').Length;
    }

    private Failure Check(JsonElement schema, SpecNode node)
    {
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return new Failure(node.Path, "no value is allowed here");

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            Failure referenced = Check(Resolve(reference.GetString()), node);
            if (referenced != null) return referenced;
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            Failure typed = CheckType(type, node);
            if (typed != null) return typed;
        }

        if (schema.TryGetProperty("enum", out JsonElement allowed))
        {
            bool match = false;
            foreach (JsonElement candidate in allowed.EnumerateArray())
            {
                if (Equal(candidate, node))
                {
                    match = true;
                    break;
                }
            }

            if (!match) return new Failure(node.Path, node.Describe() + " is not one of " + allowed.GetRawText());
        }

        if (schema.TryGetProperty("pattern", out JsonElement pattern) && node.Kind == SpecNodeKind.String)
        {
            if (!Regex.IsMatch(node.Text, pattern.GetString(), RegexOptions.CultureInvariant))
            {
                return new Failure(node.Path, "'" + Shorten(node.Text) + "' does not match pattern " + pattern.GetString());
            }
        }

        if (node.Kind == SpecNodeKind.Integer || node.Kind == SpecNodeKind.Number)
        {
            Failure range = CheckRange(schema, node);
            if (range != null) return range;
        }

        if (node.Kind == SpecNodeKind.Mapping)
        {
            Failure mapping = CheckMapping(schema, node);
            if (mapping != null) return mapping;
        }

        if (node.Kind == SpecNodeKind.Sequence && schema.TryGetProperty("items", out JsonElement items))
        {
            foreach (SpecNode item in node.Items)
            {
                Failure itemFailure = Check(items, item);
                if (itemFailure != null) return itemFailure;
            }
        }

        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement part in allOf.EnumerateArray())
            {
                Failure partFailure = Check(part, node);
                if (partFailure != null) return partFailure;
            }
        }

        if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
        {
            Failure best = null;
            foreach (JsonElement part in anyOf.EnumerateArray())
            {
                Failure partFailure = Check(part, node);
                if (partFailure == null)
                {
                    best = null;
                    goto anyOfDone;
                }

                if (best == null || partFailure.Depth > best.Depth) best = partFailure;
            }

            return best;
        }

        anyOfDone:
        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int matches = 0;
            Failure best = null;
            foreach (JsonElement part in oneOf.EnumerateArray())
            {
                Failure partFailure = Check(part, node);
                if (partFailure == null)
                {
                    matches++;
                }
                else if (best == null || partFailure.Depth > best.Depth)
                {
                    best = partFailure;
                }
            }

            if (matches == 0) return best;
            if (matches > 1) return new Failure(node.Path, node.Describe() + " matches more than one alternative of a oneOf");
        }

        return null;
    }

    private Failure CheckMapping(JsonElement schema, SpecNode node)
    {
        HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
        CollectProperties(schema, known);

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (node[name.GetString()] == null)
                {
                    return new Failure(node.Path, "required property '" + name.GetString() + "' is missing");
                }
            }
        }

        bool closed = (schema.TryGetProperty("additionalProperties", out JsonElement additional)
                       && additional.ValueKind == JsonValueKind.False)
                      || (schema.TryGetProperty("unevaluatedProperties", out JsonElement unevaluated)
                          && unevaluated.ValueKind == JsonValueKind.False);

        schema.TryGetProperty("properties", out JsonElement properties);
        foreach (KeyValuePair<string, SpecNode> entry in node.Entries)
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(entry.Key, out JsonElement propertySchema))
            {
                Failure propertyFailure = Check(propertySchema, entry.Value);
                if (propertyFailure != null) return propertyFailure;
            }
            else if (closed && !known.Contains(entry.Key))
            {
                return new Failure(entry.Value.Path, "property '" + entry.Key + "' is not allowed here");
            }
        }

        return null;
    }

    // Properties declared by this schema and, for unevaluatedProperties, by its $ref / allOf parts.
    private void CollectProperties(JsonElement schema, HashSet<string> names)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject()) names.Add(property.Name);
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference)) CollectProperties(Resolve(reference.GetString()), names);
        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement part in allOf.EnumerateArray()) CollectProperties(part, names);
        }
    }

    private static Failure CheckType(JsonElement type, SpecNode node)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement option in type.EnumerateArray())
            {
                if (IsType(option.GetString(), node)) return null;
            }

            return new Failure(node.Path, "expected one of " + type.GetRawText() + ", found " + node.Describe());
        }

        return IsType(type.GetString(), node)
            ? null
            : new Failure(node.Path, "expected " + type.GetString() + ", found " + node.Describe());
    }

    private static bool IsType(string type, SpecNode node) =>
        type switch
        {
            "object" => node.Kind == SpecNodeKind.Mapping,
            "array" => node.Kind == SpecNodeKind.Sequence,
            "string" => node.Kind == SpecNodeKind.String,
            "integer" => node.Kind == SpecNodeKind.Integer,
            "number" => node.Kind == SpecNodeKind.Integer || node.Kind == SpecNodeKind.Number,
            "boolean" => node.Kind == SpecNodeKind.Boolean,
            "null" => node.Kind == SpecNodeKind.Null,
            _ => false,
        };

    private static Failure CheckRange(JsonElement schema, SpecNode node)
    {
        decimal value;
        if (node.Kind == SpecNodeKind.Integer)
        {
            if (node.Text.StartsWith("-", StringComparison.Ordinal))
            {
                value = decimal.Parse(node.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            else
            {
                value = node.AsUInt64();
            }
        }
        else if (!decimal.TryParse(node.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        if (schema.TryGetProperty("minimum", out JsonElement minimum) && value < minimum.GetDecimal())
        {
            return new Failure(node.Path, node.Text + " is below the minimum " + minimum.GetRawText());
        }

        if (schema.TryGetProperty("maximum", out JsonElement maximum) && value > maximum.GetDecimal())
        {
            return new Failure(node.Path, node.Text + " is above the maximum " + maximum.GetRawText());
        }

        return null;
    }

    private static bool Equal(JsonElement candidate, SpecNode node) =>
        candidate.ValueKind switch
        {
            JsonValueKind.String => node.Kind == SpecNodeKind.String && candidate.GetString() == node.Text,
            JsonValueKind.Null => node.Kind == SpecNodeKind.Null,
            JsonValueKind.True => node.Kind == SpecNodeKind.Boolean && node.Text.Equals("true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => node.Kind == SpecNodeKind.Boolean && node.Text.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => node.Kind == SpecNodeKind.Integer && candidate.GetRawText() == node.AsUInt64().ToString(CultureInfo.InvariantCulture),
            _ => false,
        };

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only local $ref values are supported: " + reference);
        }

        JsonElement current = root;
        foreach (string part in reference.Substring(2).Split('/'))
        {
            current = current.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
        }

        return current;
    }

    private static string Shorten(string text)
    {
        string flat = text.Replace("\n", "\\n");
        return flat.Length <= 60 ? flat : flat.Substring(0, 57) + "...";
    }
}
