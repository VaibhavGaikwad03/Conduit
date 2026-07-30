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

    private const int MaxResults = 100;   // cap work + payload
    private const int MaxTokens = 1000;   // bounded id → path memory
    private const int MinQueryLen = 2;

    private readonly ILogger _log = ConduitLog.For("FileSearch");
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Queue<string> _tokenOrder = new();

    // The user folders we search — where personal files actually live (not system/program dirs).
    private static readonly string[] Roots = BuildRoots();

    /// <summary>Finds files whose name contains <paramref name="query"/>; registers each as a token.</summary>
    public (IReadOnlyList<Result> Results, bool Truncated) Search(string query)
    {
        var results = new List<Result>();
        query = (query ?? "").Trim();
        if (query.Length < MinQueryLen) return (results, false);

        bool truncated = false;
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in SafeEnumerateFiles(root))
            {
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
    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
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
