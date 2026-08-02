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
/// A route is a plain text file. Thus a person or a script can write it, and a diff gives
/// exactly what changed between two benchmark runs.
///
///   # name           x       y     z       yawDeg  pitchDeg
///   ridge-north      512400  180   512400  0       -10
///
/// The yaw and the pitch are in degrees, because a person reads them. This code converts them
/// to the radians that the engine uses.
///
/// A yaw of 0 faces north, and the value increases counter-clockwise. A negative pitch looks
/// down.
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

    static float Deg(string s) =>
        (float)double.Parse(s, CultureInfo.InvariantCulture) * Vintagestory.API.MathTools.GameMath.DEG2RAD;
}
