namespace MusicHoarder.Api.Review.Audit;

internal sealed class CopyAuditCheckException(string code, string stage = "paths") : Exception(code)
{
    internal string Code { get; } = code;
    internal string Stage { get; } = stage;
}

/// <summary>Read-only, exact-case traversal. A configured root may itself resolve through a
/// platform alias; descendant symlinks must stay inside that root's physical tree.</summary>
internal static class CopyAuditPaths
{
    internal static string SourceRoot(string path, params string[] configuredRoots)
    {
        var roots = configuredRoots.Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0) throw new CopyAuditCheckException("root_not_configured");
        return roots.Where(r => path.StartsWith(r == "/" ? "/" : r + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(r => r.Length).FirstOrDefault() ?? throw new CopyAuditCheckException("path_outside_root");
    }

    internal static string Check(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new CopyAuditCheckException("root_not_configured");
        var absoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path ||
            !path.StartsWith(absoluteRoot == "/" ? "/" : absoluteRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CopyAuditCheckException("path_outside_root");
        if (!Directory.Exists(absoluteRoot)) throw new CopyAuditCheckException("root_unavailable");
        var physicalRoot = Resolve(absoluteRoot, null);
        return Resolve(path, physicalRoot, absoluteRoot == "/" ? 0 : absoluteRoot.Length);
    }
    private static string Resolve(string path, string? boundary, int rootLength = 0)
    {
        var current = boundary ?? Path.GetPathRoot(path)!;
        var remaining = boundary is null ? path[current.Length..] : path[(rootLength + 1)..];
        foreach (var part in remaining.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current)) throw new DirectoryNotFoundException();
            // A case-insensitive NAS/host must not silently adopt an alternative spelling.
            var exact = false; var folded = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, part, StringComparison.Ordinal)) { exact = true; break; }
                folded |= string.Equals(name, part, StringComparison.OrdinalIgnoreCase);
            }
            if (!exact)
            {
                if (folded) throw new CopyAuditCheckException("path_case_mismatch");
                throw new FileNotFoundException();
            }
            current = Path.Combine(current, part);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                current = Resolve(info.ResolveLinkTarget(true)?.FullName ?? throw new CopyAuditCheckException("unresolved_symlink"), null);
            }
            if (boundary is not null && current != boundary && !current.StartsWith(boundary == "/" ? "/" : boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new CopyAuditCheckException("symlink_outside_root");
        }
        return current;
    }
}
