using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VenusMusicVideo.Maps;

/// <summary>
/// The IAU/USGS Gazetteer of Planetary Nomenclature for Venus: every named feature (about two thousand) with its
/// centre, size, type and the origin of its name. Read from the USGS "center points" shapefile download - only its
/// attribute table (the .dbf inside the zip) is needed, which is a simple fixed-width record file.
/// </summary>
internal sealed class Gazetteer
{
    /// <summary>The download (USGS Astrogeology; public domain).</summary>
    public const string Url = "https://asc-planetarynames-data.s3.us-west-2.amazonaws.com/VENUS_nomenclature_center_pts.zip";

    private Gazetteer(List<NamedFeature> features)
    {
        Features = features;
    }

    /// <summary>The gazetteer loaded at start-up, or null when it is not available.</summary>
    public static Gazetteer Current { get; set; }

    /// <summary>Every named feature.</summary>
    public IReadOnlyList<NamedFeature> Features { get; }

    /// <summary>Finds a feature by name (case and spacing ignored), or null.</summary>
    public NamedFeature Find(string name)
    {
        string key = Normalize(name);
        return Features.FirstOrDefault(f => Normalize(f.Name) == key);
    }

    /// <summary>Reads the gazetteer from the downloaded zip.</summary>
    /// <param name="zipPath">VENUS_nomenclature_center_pts.zip.</param>
    /// <returns>The gazetteer.</returns>
    public static Gazetteer Load(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No .dbf table in " + zipPath + ".");
        byte[] table;
        using (Stream stream = entry.Open())
        using (MemoryStream memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            table = memory.ToArray();
        }

        return new Gazetteer(ReadDbf(table));
    }

    private static List<NamedFeature> ReadDbf(byte[] d)
    {
        int count = BitConverter.ToInt32(d, 4);
        int headerLength = BitConverter.ToUInt16(d, 8);
        int recordLength = BitConverter.ToUInt16(d, 10);
        List<(string Name, int Offset, int Length)> fields = new List<(string, int, int)>();
        int offset = 1; // each record starts with a deletion flag
        for (int at = 32; d[at] != 0x0D; at += 32)
        {
            string name = Encoding.ASCII.GetString(d, at, 11).TrimEnd('\0');
            int length = d[at + 16];
            fields.Add((name, offset, length));
            offset += length;
        }

        int Field(string name) => fields.FindIndex(f => f.Name == name);
        int iName = Field("clean_name");
        int iLat = Field("center_lat");
        int iLon = Field("center_lon");
        int iDiameter = Field("diameter");
        int iType = Field("type");
        int iOrigin = Field("origin");
        int iApproval = Field("approval");
        if (iName < 0 || iLat < 0 || iLon < 0) throw new InvalidDataException("The gazetteer table has no name or centre columns.");

        List<NamedFeature> features = new List<NamedFeature>(count);
        for (int i = 0; i < count; i++)
        {
            int record = headerLength + (i * recordLength);
            if (d[record] == (byte)'*') continue; // deleted
            string Text(int field) => field < 0 ? string.Empty : Encoding.UTF8.GetString(d, record + fields[field].Offset, fields[field].Length).Trim();
            double Number(int field) => double.TryParse(Text(field), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            string approval = Text(iApproval);
            if (approval.Length > 0 && !approval.StartsWith("Adopted", StringComparison.OrdinalIgnoreCase)) continue;
            double lon = Number(iLon);
            if (lon > 180) lon -= 360;
            string type = Text(iType);
            int comma = type.IndexOf(',');
            features.Add(new NamedFeature(Text(iName), Number(iLat), lon, Number(iDiameter), comma > 0 ? type.Substring(0, comma) : type, Text(iOrigin)));
        }

        return features;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>One named feature.</summary>
/// <param name="Name">The name, e.g. "Maat Mons".</param>
/// <param name="Lat">Centre latitude, degrees north.</param>
/// <param name="Lon">Centre longitude, degrees east (-180..180).</param>
/// <param name="DiameterKm">Size, km.</param>
/// <param name="Type">Feature type, e.g. "Mons", "Corona", "Crater".</param>
/// <param name="Origin">Where the name comes from, e.g. "Egyptian goddess of truth and justice."</param>
internal sealed record NamedFeature(string Name, double Lat, double Lon, double DiameterKm, string Type, string Origin)
{
    /// <summary>True when the name is a goddess's.</summary>
    public bool IsGoddess => Origin.Contains("goddess", StringComparison.OrdinalIgnoreCase);

    /// <summary>Words in a name's origin that mark it as a woman's or a female figure's (IAU: most Venus names).</summary>
    private static readonly string[] FemaleWords =
    {
        "goddess", "titaness", "heroine", "woman", "women", "female", "girl", "wife", "daughter", "mother", "sister",
        "queen", "princess", "priestess", "giantess", "mermaid", "nymph", "sorceress", "witch", "fairy", "maiden",
        "first name", "actress", "writer", "poet", "scientist", "painter", "singer", "dancer",
    };

    /// <summary>True when the place is named after a goddess, a woman or another female figure.</summary>
    public bool IsWomansName => IsGoddess || Array.Exists(FemaleWords, w => Origin.Contains(w, StringComparison.OrdinalIgnoreCase));
}
