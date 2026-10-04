namespace RCN1Bridge.Core.Diagnostics;

public static class Log
{
    private const int RecentCapacity = 300;
    private static readonly Queue<string> Recent = new();
    private static readonly Lock Gate = new();

    // Raised on whatever thread logged
    public static event Action<string>? Written;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    public static IReadOnlyList<string> Tail(int count)
    {
        lock (Gate)
            return Recent.Skip(Math.Max(0, Recent.Count - count)).ToArray();
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentCapacity)
                Recent.Dequeue();
        }
        Written?.Invoke(line);
    }
}
