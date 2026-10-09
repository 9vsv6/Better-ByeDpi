using System.IO;

namespace ByeDpiPc.Core;

/// <summary>In-memory log shown on the Logs tab, mirrored to %AppData%\ByeDPI-PC\last.log.</summary>
public static class Log
{
    private const int MaxLines = 2000;
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Lines = new();
    private static StreamWriter? _file;

    public static event Action<string>? LineAdded;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            Lines.AddLast(line);
            if (Lines.Count > MaxLines) Lines.RemoveFirst();
            try
            {
                if (_file == null)
                {
                    System.IO.Directory.CreateDirectory(AppSettings.Directory);
                    _file = new StreamWriter(Path.Combine(AppSettings.Directory, "last.log"), append: false) { AutoFlush = true };
                }
                _file.WriteLine(line);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
        LineAdded?.Invoke(line);
    }

    public static string Snapshot()
    {
        lock (Gate) return string.Join(Environment.NewLine, Lines);
    }

    public static void Clear()
    {
        lock (Gate) Lines.Clear();
    }
}
