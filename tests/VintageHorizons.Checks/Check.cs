namespace VintageHorizons.Checks;

/// <summary>
/// The count of the assertions for one suite.
///
/// This class is very small on purpose. This repository has no NuGet dependency at all, and
/// the local package cache holds no test framework. Thus a framework makes the fast tier need
/// a network before it can report anything.
///
/// An assertion never throws. A suite whose third assertion fails must still report the
/// fourth one. When a shared rule breaks, the shape of the full set of failures points at the
/// cause. A stop at the first failure hides that shape.
/// </summary>
public sealed class Check
{
    public int Passed;
    public readonly List<string> Failures = new();

    void Fail(string what, string detail) => Failures.Add(what + "\n        " + detail);

    public void True(bool condition, string what)
    {
        if (condition) Passed++;
        else Fail(what, "expected true, got false");
    }

    public void False(bool condition, string what) => True(!condition, what);

    public void Eq<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) Passed++;
        else Fail(what, $"expected {Show(expected)}, got {Show(actual)}");
    }

    public void Near(double expected, double actual, double tolerance, string what)
    {
        if (Math.Abs(expected - actual) <= tolerance) Passed++;
        else Fail(what, $"expected {expected} +/- {tolerance}, got {actual}");
    }

    public void SeqEq<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string what)
    {
        if (expected.Count != actual.Count)
        {
            Fail(what, $"length {expected.Count} expected, got {actual.Count}"
                + $"\n        expected [{string.Join(", ", expected.Select(Show))}]"
                + $"\n        actual   [{string.Join(", ", actual.Select(Show))}]");
            return;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
            {
                Fail(what, $"differs at [{i}]: expected {Show(expected[i])}, got {Show(actual[i])}"
                    + $"\n        expected [{string.Join(", ", expected.Select(Show))}]"
                    + $"\n        actual   [{string.Join(", ", actual.Select(Show))}]");
                return;
            }
        }
        Passed++;
    }

    /// <summary>
    /// Asserts that the call does not throw.
    ///
    /// Use this where the contract is "return null for bad input", and not "throw". A
    /// deserializer that throws, instead of a return of null, stops the storage thread. That
    /// difference is the reason for this method.
    /// </summary>
    public void NoThrow(Action action, string what)
    {
        try
        {
            action();
            Passed++;
        }
        catch (Exception e)
        {
            Fail(what, "threw " + e.GetType().Name + ": " + e.Message);
        }
    }

    public void Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
            Fail(what, "expected " + typeof(T).Name + ", nothing was thrown");
        }
        catch (T)
        {
            Passed++;
        }
        catch (Exception e)
        {
            Fail(what, "expected " + typeof(T).Name + ", got " + e.GetType().Name + ": " + e.Message);
        }
    }

    static string Show<T>(T value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        double d => d.ToString("0.####"),
        float f => f.ToString("0.####"),
        _ => value.ToString() ?? "?",
    };
}
