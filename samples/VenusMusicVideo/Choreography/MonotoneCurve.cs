using System;

namespace VenusMusicVideo.Choreography;

/// <summary>
/// A monotone piecewise-cubic (PCHIP, Fritsch-Butland) curve through keyed values: smooth (C1), never overshoots
/// a key (so an altitude curve never dips under its lowest key), and eases to a stop at local extremes and at both
/// ends. Before the first key it holds the first value, after the last the last.
/// </summary>
internal sealed class MonotoneCurve
{
    private readonly double[] times;
    private readonly double[] values;
    private readonly double[] slopes;

    /// <summary>Builds the curve.</summary>
    /// <param name="times">Strictly increasing key times.</param>
    /// <param name="values">One value per key.</param>
    /// <param name="monotone">
    /// True (the default): never overshoot, ease to a stop at local extremes. False: a smooth spline that keeps
    /// moving through a key (for the route, so a turn is a curve, not a stop-and-turn).
    /// </param>
    public MonotoneCurve(double[] times, double[] values, bool monotone = true)
    {
        if (times.Length != values.Length || times.Length == 0) throw new ArgumentException("One value per key time, at least one key.");
        this.times = times;
        this.values = values;
        int n = times.Length;
        slopes = new double[n];
        if (n < 2) return;
        double[] h = new double[n - 1];
        double[] d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            h[k] = times[k + 1] - times[k];
            if (h[k] <= 0) throw new ArgumentException("Key times must increase (at " + times[k + 1] + ").");
            d[k] = (values[k + 1] - values[k]) / h[k];
        }

        for (int k = 1; k < n - 1; k++)
        {
            if (!monotone)
            {
                // Non-uniform cardinal spline: the time-weighted average of the neighbouring slopes.
                slopes[k] = ((h[k] * d[k - 1]) + (h[k - 1] * d[k])) / (h[k - 1] + h[k]);
                continue;
            }

            if (d[k - 1] * d[k] <= 0)
            {
                slopes[k] = 0;
                continue;
            }

            double w1 = (2 * h[k]) + h[k - 1];
            double w2 = h[k] + (2 * h[k - 1]);
            slopes[k] = (w1 + w2) / ((w1 / d[k - 1]) + (w2 / d[k]));
        }

        slopes[0] = 0;
        slopes[n - 1] = 0;
    }

    /// <summary>The value at a time.</summary>
    /// <param name="t">The time.</param>
    /// <returns>The value.</returns>
    public double Evaluate(double t)
    {
        int n = times.Length;
        if (n == 1 || t <= times[0]) return values[0];
        if (t >= times[n - 1]) return values[n - 1];
        int lo = 0;
        int hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= t) lo = mid;
            else hi = mid;
        }

        double span = times[hi] - times[lo];
        double s = (t - times[lo]) / span;
        double s2 = s * s;
        double s3 = s2 * s;
        double h00 = (2 * s3) - (3 * s2) + 1;
        double h10 = s3 - (2 * s2) + s;
        double h01 = (-2 * s3) + (3 * s2);
        double h11 = s3 - s2;
        return (h00 * values[lo]) + (h10 * span * slopes[lo]) + (h01 * values[hi]) + (h11 * span * slopes[hi]);
    }
}
