using System;
using System.Collections.Generic;

namespace BozjaAssistant;

/// <summary>
/// A small bounded rolling log shown in the plugin UI.
///
/// Deliberately in-memory only: the Dalamud log already persists diagnostics, and
/// this list exists purely to give the user a readable "what is it doing right
/// now" trace without digging through the log file.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 300;

    private readonly LinkedList<LogEntry> entries = new();
    private readonly object gate = new();

    public enum Level
    {
        Info,
        Success,
        Warning,
        Error,
    }

    public sealed record LogEntry(DateTime Timestamp, Level Level, string Message);

    /// <summary>Snapshot of the log, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (this.gate)
        {
            return new List<LogEntry>(this.entries);
        }
    }

    public void Add(string message) => this.Push(Level.Info, message);

    public void Success(string message) => this.Push(Level.Success, message);

    public void Warning(string message) => this.Push(Level.Warning, message);

    public void Error(string message) => this.Push(Level.Error, message);

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
        }
    }

    private void Push(Level level, string message)
    {
        lock (this.gate)
        {
            this.entries.AddLast(new LogEntry(DateTime.Now, level, message));
            while (this.entries.Count > MaxEntries)
            {
                this.entries.RemoveFirst();
            }
        }
    }
}
