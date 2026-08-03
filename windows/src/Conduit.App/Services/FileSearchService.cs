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

    /// <summary>A listed directory: its name, breadcrumb path, parent token (null at the top),
    /// its immediate children (folders first), and an optional error.</summary>
    public sealed record Listing(string Name, string Path, string? Parent, IReadOnlyList<Entry> Entries, string? Error);

    /// <summary>Reserved token that opens the top level (the drive list). Empty token opens home.</summary>
    public const string RootToken = "@root";

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
    /// Lists a directory one level deep. An empty <paramref name="token"/> opens the default
    /// landing folder (the user's home folder); <c>"@root"</c> opens the top level (every drive);
    /// otherwise it lists the folder that token was handed out for. Each child is registered under
    /// a fresh token — folders for <see cref="List"/>, files for download — so the peer can only
    /// ever descend or pull something we just offered. The reply also carries the breadcrumb path
    /// and the parent token, so the requester can navigate "up" without tracking any state.
    /// </summary>
    public Listing List(string? token)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) return ListDir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (token == RootToken) return ListRoots();

        var dir = ResolveDir(token);
        if (dir is null || !Directory.Exists(dir))
            return new Listing("", "", RootToken, Array.Empty<Entry>(), "That folder is no longer available.");
        return ListDir(dir);
    }

    /// <summary>Lists one real directory, computing its breadcrumb and the token to go up.</summary>
    private Listing ListDir(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return new Listing("", "This PC", RootToken, Array.Empty<Entry>(), "That folder is no longer available.");

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
            return new Listing(DirName(dir), Breadcrumb(dir), ParentToken(dir), entries, "Couldn't read that folder.");
        }

        _log.Information("Listed {Dir} -> {Count} entr(ies)", dir, entries.Count);
        return new Listing(DirName(dir), Breadcrumb(dir), ParentToken(dir), entries, null);
    }

    /// <summary>The top-level: every ready drive (C:\, D:\, …), each a descendable token.</summary>
    private Listing ListRoots()
    {
        var entries = new List<Entry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue; // skip empty CD/card readers, disconnected network drives
                entries.Add(new Entry(RegisterDir(drive.RootDirectory.FullName), DriveLabel(drive), true, 0, ""));
            }
            catch { /* a drive we can't query — skip it */ }
        }
        // At the very top there is no parent to go up to.
        return new Listing("This PC", "This PC", null, entries, entries.Count == 0 ? "No drives available." : null);
    }

    /// <summary>The token to list <paramref name="dir"/>'s parent, or "@root" (This PC) at a drive root.</summary>
    private string ParentToken(string dir)
    {
        var parent = Directory.GetParent(dir);
        return parent is null ? RootToken : RegisterDir(parent.FullName);
    }

    /// <summary>A display name for a folder — the drive label at a drive root, else the folder name.</summary>
    private static string DirName(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(name)) return name;
        try { return DriveLabel(new DriveInfo(dir)); } catch { return dir; }
    }

    /// <summary>Builds the "This PC / Local Disk (C:) / Users / …" breadcrumb for a full path.</summary>
    private static string Breadcrumb(string dir)
    {
        var root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root)) return "This PC / " + dir;
        string driveLabel;
        try { driveLabel = DriveLabel(new DriveInfo(root)); } catch { driveLabel = root.TrimEnd('\\', '/'); }
        var crumb = "This PC / " + driveLabel;
        var rel = dir.Substring(root.Length).Trim('\\', '/');
        if (rel.Length > 0)
            foreach (var seg in rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                crumb += " / " + seg;
        return crumb;
    }

    /// <summary>A friendly drive name like "Local Disk (C:)" or "MyUSB (E:)".</summary>
    private static string DriveLabel(DriveInfo d)
    {
        var letter = d.Name.TrimEnd('\\', '/'); // "C:"
        string label;
        try { label = d.VolumeLabel; } catch { label = ""; }
        if (string.IsNullOrWhiteSpace(label))
            label = d.DriveType == DriveType.Removable ? "Removable Disk"
                  : d.DriveType == DriveType.Network ? "Network Drive"
                  : "Local Disk";
        return $"{label} ({letter})";
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
