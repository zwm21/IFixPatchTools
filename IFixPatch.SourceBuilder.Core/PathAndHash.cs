using System.Security.Cryptography;

namespace IFixPatch.SourceBuilder.Core;

public static class PathAndHash
{
    internal static void RequireAssemblyFileName(string? name, string label)
    {
        // Both path syntaxes are rejected so a manifest stays a simple name across platforms.
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new InvalidDataException(label + " cannot be used as a file name: " + name);
    }

    public static string ResolveFromFile(string ownerFile, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"A path in {ownerFile} is empty.");
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ownerFile))!, path));
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static ulong ReadUInt64Prefix(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        return reader.ReadUInt64();
    }

    public static SourceBuilderProfile LoadResolvedProfile(string path)
    {
        var profile = SourceBuilderJson.Load<SourceBuilderProfile>(path);
        profile.AssemblyPath = ResolveFromFile(path, profile.AssemblyPath);
        profile.IFixCoreAssemblyPath = ResolveFromFile(path, profile.IFixCoreAssemblyPath);
        if (profile.KnownPatchPath is not null)
            profile.KnownPatchPath = ResolveFromFile(path, profile.KnownPatchPath);
        profile.ReferenceDirectories = profile.ReferenceDirectories
            .Select(p => ResolveFromFile(path, p)).Distinct(StringComparer.Ordinal).ToList();
        return profile;
    }

    public static PatchManifest LoadResolvedManifest(string path)
    {
        var manifest = SourceBuilderJson.Load<PatchManifest>(path);
        manifest.Sources = ResolveSources(path, manifest.Sources);
        return manifest;
    }

    /// <summary>
    /// 解析 manifest 的 sources。除字面路径外支持通配：<c>*.cs</c>（当前目录）、
    /// <c>Util/*.cs</c>、<c>**/*.cs</c>（递归）、<c>Collect/**/*.cs</c>。
    ///
    /// <c>**</c> 只能作为最后一个目录段，其余目录段必须是字面量，便于核对编译输入。
    ///
    /// 顺序规则：字面条目按 manifest 里的书写顺序，通配展开的结果在各自条目内部按序数排序，
    /// 整体按首次出现去重。csc 的输入顺序会影响产物，展开顺序不稳定会让 patch 哈希漂。
    /// </summary>
    public static List<string> ResolveSources(string ownerFile, IEnumerable<string> patterns)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(ownerFile))!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<string>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                throw new InvalidDataException($"A source entry in {ownerFile} is empty.");

            if (!IsPattern(pattern))
            {
                var literal = ResolveFromFile(ownerFile, pattern);
                if (seen.Add(literal))
                    resolved.Add(literal);
                continue;
            }

            var matches = Expand(ownerFile, root, pattern);
            if (matches.Count == 0)
                throw new InvalidDataException(
                    $"Source pattern \"{pattern}\" in {ownerFile} matched no files.");
            foreach (var match in matches)
                if (seen.Add(match))
                    resolved.Add(match);
        }

        if (resolved.Count == 0)
            throw new InvalidDataException($"{ownerFile} resolved to no patch sources.");
        return resolved;
    }

    private static bool IsPattern(string value) => value.Contains('*') || value.Contains('?');

    private static List<string> Expand(string ownerFile, string root, string pattern)
    {
        var segments = pattern.Replace('\\', '/').Split('/');
        var filePattern = segments[^1];
        if (filePattern.Length == 0)
            throw new InvalidDataException(
                $"Source pattern \"{pattern}\" in {ownerFile} has no file name part.");

        var directories = segments[..^1];
        var recursive = false;
        for (var i = 0; i < directories.Length; i++)
        {
            if (directories[i] == "**")
            {
                if (i != directories.Length - 1)
                    throw new NotSupportedException(
                        $"Source pattern \"{pattern}\" in {ownerFile}: \"**\" is only supported as the " +
                        "last directory segment.");
                recursive = true;
                continue;
            }
            if (IsPattern(directories[i]))
                throw new NotSupportedException(
                    $"Source pattern \"{pattern}\" in {ownerFile}: wildcards in directory names are not " +
                    "supported; only \"**\" as the last directory segment is.");
        }

        var literalDirectories = recursive ? directories[..^1] : directories;
        var baseDirectory = Path.GetFullPath(Path.Combine([root, .. literalDirectories]));
        if (!Directory.Exists(baseDirectory))
            throw new DirectoryNotFoundException(
                $"Source pattern \"{pattern}\" in {ownerFile} points at a missing directory: {baseDirectory}");

        return Directory
            .EnumerateFiles(
                baseDirectory,
                filePattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
