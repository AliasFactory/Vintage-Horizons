using System.Globalization;

namespace VintageHorizonsBench;

public class BenchWaypoint
{
    public string Name = "";
    public double X;
    public double Y;
    public double Z;
    public float Yaw;
    public float Pitch;
}

/// <summary>
/// A route is a plain text file so it can be written by hand or by a script, and so a
/// diff shows exactly what changed between benchmark runs:
///
///   # name           x       y     z       yawDeg  pitchDeg
///   ridge-north      512400  180   512400  0       -10
///
/// Yaw/pitch are degrees for legibility and converted to the engine's radians here.
/// Yaw 0 faces north, increasing counter-clockwise; pitch is negative looking down.
/// </summary>
public class BenchRoute
{
    public readonly List<BenchWaypoint> Waypoints = new();

    public static BenchRoute Load(string path)
    {
        var route = new BenchRoute();

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                throw new FormatException($"Route line needs at least 'name x y z': {rawLine}");
            }

            route.Waypoints.Add(new BenchWaypoint
            {
                Name = parts[0],
                X = double.Parse(parts[1], CultureInfo.InvariantCulture),
                Y = double.Parse(parts[2], CultureInfo.InvariantCulture),
                Z = double.Parse(parts[3], CultureInfo.InvariantCulture),
                Yaw = parts.Length > 4 ? Deg(parts[4]) : 0f,
                Pitch = parts.Length > 5 ? Deg(parts[5]) : 0f,
            });
        }

        if (route.Waypoints.Count == 0) throw new FormatException($"Route {path} has no waypoints");
        return route;
    }

    static float Deg(string s) => (float)(double.Parse(s, CultureInfo.InvariantCulture) * Math.PI / 180.0);
}
