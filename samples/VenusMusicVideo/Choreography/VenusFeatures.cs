using System;
using System.Collections.Generic;
using System.Linq;

namespace VenusMusicVideo.Choreography;

/// <summary>
/// Named places on Venus (approximate centres; latitude north-positive, longitude EAST-positive in -180..180, the
/// convention of the Magellan maps the textures come from). <c>flight.json</c> keys can say <c>"over": "Maxwell
/// Montes"</c>, and the preview names the nearest place under the camera.
/// </summary>
internal static class VenusFeatures
{
    /// <summary>
    /// The places. Nearly every feature on Venus is named after a goddess or a famous woman; the three exceptions
    /// (Maxwell Montes, Alpha Regio, Beta Regio) are marked as not goddess-named, so they get no label.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, double Lat, double Lon, bool Goddess)> All = new (string, double, double, bool)[]
    {
        ("Maxwell Montes", 65.2, 3.3, false),
        ("Ishtar Terra", 70.4, 27.5, true),
        ("Lakshmi Planum", 68.6, -20.7, true),
        ("Fortuna Tessera", 69.9, 45.1, true),
        ("Tellus Tessera", 42.6, 76.8, true),
        ("Bell Regio", 32.8, 51.4, true),
        ("Eistla Regio", 10.5, 21.5, true),
        ("Aphrodite Terra", -5.8, 104.8, true),
        ("Ovda Regio", -2.8, 85.8, true),
        ("Thetis Regio", -11.4, 129.9, true),
        ("Diana Chasma", -14.5, 155.8, true),
        ("Artemis Corona", -41.2, 135.1, true),
        ("Atla Regio", 9.2, -159.9, true),
        ("Maat Mons", 0.5, -165.4, true),
        ("Sapas Mons", 8.5, -171.5, true),
        ("Ulfrun Regio", 27.0, -135.0, true),
        ("Asteria Regio", 21.6, -92.5, true),
        ("Beta Regio", 25.3, -77.2, false),
        ("Theia Mons", 22.7, -78.6, true),
        ("Phoebe Regio", -6.0, -77.2, true),
        ("Themis Regio", -37.4, -75.8, true),
        ("Imdr Regio", -43.0, -147.4, true),
        ("Alpha Regio", -22.0, 4.7, false),
        ("Lada Terra", -67.0, 20.0, true),
        ("Gula Mons", 21.9, -0.8, true),
        ("Sif Mons", 22.0, -7.6, true),
    };

    /// <summary>Finds a place by name (case and spacing ignored).</summary>
    /// <param name="name">The name.</param>
    /// <returns>Its latitude and longitude.</returns>
    /// <exception cref="ArgumentException">Unknown name.</exception>
    public static (double Lat, double Lon) Find(string name)
    {
        string key = Normalize(name);
        foreach ((string n, double lat, double lon, _) in All)
        {
            if (Normalize(n) == key) return (lat, lon);
        }

        throw new ArgumentException("Unknown place '" + name + "'. Known: " + string.Join(", ", All.Select(f => f.Name)) + " (or use \"over\": [lat, lon]).");
    }

    /// <summary>The nearest named place within a distance, or null.</summary>
    /// <param name="lat">Latitude, degrees.</param>
    /// <param name="lon">East longitude, degrees.</param>
    /// <param name="withinDegrees">The largest great-circle distance.</param>
    /// <returns>The name, or null.</returns>
    public static string Nearest(double lat, double lon, double withinDegrees = 12)
    {
        string best = null;
        double bestDistance = withinDegrees;
        foreach ((string n, double flat, double flon, _) in All)
        {
            double d = Distance(lat, lon, flat, flon);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = n;
            }
        }

        return best;
    }

    /// <summary>Great-circle distance in degrees.</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double r = Math.PI / 180;
        double c = (Math.Sin(lat1 * r) * Math.Sin(lat2 * r)) + (Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Cos((lon2 - lon1) * r));
        return Math.Acos(Math.Clamp(c, -1, 1)) / r;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
