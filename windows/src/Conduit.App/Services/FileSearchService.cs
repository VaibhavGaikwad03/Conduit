using System.IO;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Backs the cross-device file-search feature on the Windows side: finds files under the
/// user's folders whose name contains a query, and remembers each hit under an opaque id so
/// the peer can download it. A <c>file-request</c> is only honored for an id we handed out in a
/// recent result, so a peer can never pull an arbitrary path off this machine.
/// </summary>
public sealed class FileSearchService
{
    public sealed record Result(string Id, string Name, long Size, string Folder, string Mime);

    /// <summary>One row in a directory listing: a folder or a file, each with a download/descend token.</summary>
    public sealed record Entry(string Token, string Name, bool IsDir, long Size, string Mime);

    /// <summary>A listed directory: its display name and its immediate children (folders first).</summary>
    public sealed record Listing(string Name, IReadOnlyList<Entry> Entries, string? Error);

    private const int MaxResults = 100;   // cap work + payload
    private const int MaxTokens = 1000;   // bounded id → path memory
    private const int MaxDirTokens = 4000; // dir tokens: enough to keep a deep browse stack valid
    private const int MaxEntries = 5000;  // cap a single huge directory's payload
    private const int MinQueryLen = 2;

    private readonly ILogger _log = ConduitLog.For("FileSearch");
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Queue<string> _tokenOrder = new();
    private readonly Dictionary<string, string> _dirTokens = new();
    private readonly Queue<string> _dirTokenOrder = new();

    // The user folders we search — where personal files actually live (not system/program dirs).
    private static readonly string[] Roots = BuildRoots();

    /// <summary>Finds files whose name contains <paramref name="query"/>; registers each as a token.</summary>
    public (IReadOnlyList<Result> Results, bool Truncated) Search(string query, CancellationToken ct = default)
    {
        var results = new List<Result>();
        query = (query ?? "").Trim();
        if (query.Length < MinQueryLen) return (results, false);

        bool truncated = false;
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in SafeEnumerateFiles(root, ct))
            {
                if (ct.IsCancellationRequested) return (results, truncated);
                var name = Path.GetFileName(path);
                if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (results.Count >= MaxResults) { truncated = true; break; }

                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; } // vanished or unreadable between listing and stat

                var id = Guid.NewGuid().ToString("N");
                Register(id, path);
                results.Add(new Result(id, name, size, FolderLabel(path), MimeFor(name)));
            }
            if (truncated) break;
        }

        _log.Information("Search '{Query}' -> {Count} result(s){Trunc}",
            query, results.Count, truncated ? " (truncated)" : "");
        return (results, truncated);
    }

    /// <summary>Full path for an id we previously returned, or null if unknown/expired.</summary>
    public string? Resolve(string id)
    {
        lock (_gate) return _tokens.TryGetValue(id, out var path) ? path : null;
    }

    // ---- Directory browsing ----------------------------------------------------

    /// <summary>
    /// Lists a directory one level deep. An empty <paramref name="token"/> returns the top-level
    /// roots (the user folders); otherwise it lists the folder that token was handed out for. Each
    /// child is registered under a fresh token — folders for <see cref="List"/>, files for download —
    /// so the peer can only ever descend or pull something we just offered.
    /// </summary>
    public Listing List(string? token)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) return ListRoots();

        var dir = ResolveDir(token);
        if (dir is null || !Directory.Exists(dir))
            return new Listing("", Array.Empty<Entry>(), "That folder is no longer available.");

        var entries = new List<Entry>();
        try
        {
            foreach (var sub in SortedOrEmpty(() => Directory.GetDirectories(dir)))
            {
                if (entries.Count >= MaxEntries) break;
                entries.Add(new Entry(RegisterDir(sub), Path.GetFileName(sub), true, 0, ""));
            }
            foreach (var file in SortedOrEmpty(() => Directory.GetFiles(dir)))
            {
                if (entries.Count >= MaxEntries) break;
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; } // vanished or unreadable between listing and stat
                var name = Path.GetFileName(file);
                entries.Add(new Entry(Register(file), name, false, size, MimeFor(name)));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to list {Dir}", dir);
            return new Listing(Path.GetFileName(dir), entries, "Couldn't read that folder.");
        }

        _log.Information("Listed {Dir} -> {Count} entr(ies)", dir, entries.Count);
        return new Listing(Path.GetFileName(dir), entries, null);
    }

    /// <summary>The top-level roots: the user folders, each a descendable directory token.</summary>
    private Listing ListRoots()
    {
        var entries = new List<Entry>();
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            entries.Add(new Entry(RegisterDir(root), Path.GetFileName(root), true, 0, ""));
        }
        return new Listing("This PC", entries, null);
    }

    /// <summary>Enumerates paths sorted by name, or an empty list if the folder can't be read.</summary>
    private static IReadOnlyList<string> SortedOrEmpty(Func<string[]> get)
    {
        try
        {
            var arr = get();
            Array.Sort(arr, (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                StringComparison.OrdinalIgnoreCase));
            return arr;
        }
        catch { return Array.Empty<string>(); }
    }

    private string? ResolveDir(string token)
    {
        lock (_gate) return _dirTokens.TryGetValue(token, out var path) ? path : null;
    }

    private string RegisterDir(string path)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _dirTokens[id] = path;
            _dirTokenOrder.Enqueue(id);
            while (_dirTokenOrder.Count > MaxDirTokens)
                _dirTokens.Remove(_dirTokenOrder.Dequeue());
        }
        return id;
    }

    /// <summary>Registers a file under a fresh token and returns it.</summary>
    private string Register(string path)
    {
        var id = Guid.NewGuid().ToString("N");
        Register(id, path);
        return id;
    }

    private void Register(string id, string path)
    {
        lock (_gate)
        {
            _tokens[id] = path;
            _tokenOrder.Enqueue(id);
            while (_tokenOrder.Count > MaxTokens)
                _tokens.Remove(_tokenOrder.Dequeue());
        }
    }

    /// <summary>Recursively lists files, quietly skipping folders we can't read.</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            var dir = stack.Pop();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = Array.Empty<string>(); }
            foreach (var sub in subdirs) stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var file in files) yield return file;
        }
    }

    /// <summary>The immediate parent folder name, for context in the results list.</summary>
    private static string FolderLabel(string path)
    {
        var dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);
    }

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private static string[] BuildRoots()
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
            {
                Folder(Environment.SpecialFolder.Desktop),
                Folder(Environment.SpecialFolder.MyDocuments),
                Folder(Environment.SpecialFolder.MyPictures),
                Folder(Environment.SpecialFolder.MyMusic),
                Folder(Environment.SpecialFolder.MyVideos),
                Path.Combine(profile, "Downloads"),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
