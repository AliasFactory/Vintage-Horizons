using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// An ILogger that records what it was told, so a check can assert on the message a player
/// or admin would actually see.
///
/// Implements the interface directly rather than extending LoggerBase, deliberately:
/// LoggerBase's static constructor throws on purpose and then reads a filename out of the
/// stack trace to find the source root. Without a PDB beside the DLL that filename is null,
/// so the NRE happens inside the catch and escapes as a TypeInitializationException - a
/// confusing failure a long way from its cause.
/// </summary>
public sealed class CaptureLogger : ILogger
{
    public readonly List<string> Lines = new();

    public bool Contains(string fragment) =>
        Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    void Record(string message) => Lines.Add(message);

    void Record(string format, params object[] args) =>
        Lines.Add(args.Length == 0 ? format : string.Format(format, args));

    public bool TraceLog { get; set; }

    // Required by the interface; nothing here subscribes. The explicit add/remove keeps the
    // compiler from warning about a field-like event that is never raised.
    public event LogEntryDelegate EntryAdded { add { } remove { } }

    public void ClearWatchers() { }

    public void Log(EnumLogType logType, string format, params object[] args) => Record(format, args);
    public void Log(EnumLogType logType, string message) => Record(message);
    public void LogException(EnumLogType logType, Exception e) => Record(e.ToString());

    public void Chat(string format, params object[] args) => Record(format, args);
    public void Chat(string message) => Record(message);
    public void Event(string format, params object[] args) => Record(format, args);
    public void Event(string message) => Record(message);
    public void StoryEvent(string format, params object[] args) => Record(format, args);
    public void StoryEvent(string message) => Record(message);
    public void Build(string format, params object[] args) => Record(format, args);
    public void Build(string message) => Record(message);
    public void VerboseDebug(string format, params object[] args) => Record(format, args);
    public void VerboseDebug(string message) => Record(message);
    public void Debug(string format, params object[] args) => Record(format, args);
    public void Debug(string message) => Record(message);
    public void Notification(string format, params object[] args) => Record(format, args);
    public void Notification(string message) => Record(message);
    public void Warning(string format, params object[] args) => Record(format, args);
    public void Warning(string message) => Record(message);
    public void Warning(Exception e) => Record(e.ToString());
    public void Error(string format, params object[] args) => Record(format, args);
    public void Error(string message) => Record(message);
    public void Error(Exception e) => Record(e.ToString());
    public void Fatal(string format, params object[] args) => Record(format, args);
    public void Fatal(string message) => Record(message);
    public void Fatal(Exception e) => Record(e.ToString());
    public void Audit(string format, params object[] args) => Record(format, args);
    public void Audit(string message) => Record(message);
}
