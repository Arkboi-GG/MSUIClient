namespace MSUIClient.Creator;

// ─────────────────────────────────────────────────────────────────────────────
// Minimal stand-ins for the ASP.NET surface the ported MangosSuperUI services
// expect (they were DI singletons there). Keeping the shims here means the
// ported files stay near-identical to upstream and future re-syncs are diffs,
// not rewrites. Everything logs to the console like the rest of the client.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Console-backed replacement for Microsoft.Extensions.Logging.ILogger&lt;T&gt;.
/// Message templates keep their {Placeholder} braces - the args are appended,
/// which is plenty for a console line.</summary>
public sealed class ILogger<T>
{
    private static string Format(string message, object?[] args) =>
        args.Length == 0 ? message : $"{message} [{string.Join(", ", args)}]";

    public void LogInformation(string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] {Format(message, args)}");
    public void LogDebug(string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] {Format(message, args)}");
    public void LogWarning(string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] WARN {Format(message, args)}");
    public void LogError(string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] ERROR {Format(message, args)}");
    public void LogError(Exception exception, string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] ERROR {Format(message, args)}: {exception.Message}");
    public void LogWarning(Exception exception, string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] WARN {Format(message, args)}: {exception.Message}");
    public void LogInformation(Exception exception, string message, params object?[] args) =>
        Console.WriteLine($"[creator:{typeof(T).Name}] {Format(message, args)}: {exception.Message}");
}

/// <summary>
/// Upstream's MpqReaderService keeps the game's archives mounted and is asked to
/// unmount/remount around a patch rebuild. MSUIClient's own MpqMount owns the
/// archives here, and the creator's export never rebuilds an archive the client
/// booted with (a fresh patch MPQ is only mounted on the next start), so the
/// unmount hooks are deliberate no-ops.
/// </summary>
public sealed class MpqReaderService
{
    public void UnmountArchive(string archiveName) { }
    public void RemountArchive(string path) { }
}
