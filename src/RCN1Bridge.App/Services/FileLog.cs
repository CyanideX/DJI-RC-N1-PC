using System.IO;
using RCN1Bridge.Core.Diagnostics;

namespace RCN1Bridge.App.Services;

public static class FileLog
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RCN1Bridge", "logs");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var old in new DirectoryInfo(Folder).GetFiles("bridge-*.log"))
                if (old.LastWriteTime < DateTime.Now.AddDays(-7))
                    old.Delete();

            var path = Path.Combine(Folder, $"bridge-{DateTime.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Log.Written += Append;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging to file is a nice-to-have; the in-memory tail still feeds Diagnostics
            Log.Warn($"File logging disabled: {ex.Message}");
        }
    }

    public static void Stop()
    {
        Log.Written -= Append;
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Append(string line)
    {
        lock (Gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
    }
}
