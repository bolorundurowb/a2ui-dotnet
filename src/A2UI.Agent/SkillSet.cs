using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A generated skill document and its destination relative to the export directory.</summary>
/// <param name="FileName">The relative path to the skill document.</param>
/// <param name="Content">The complete skill document, including any front matter.</param>
public sealed record SkillFile(string FileName, string Content);

/// <summary>Exports generated skill documents to a directory.</summary>
public static class SkillSet
{
    /// <summary>Writes skill documents and clears stale content from their containing directories.</summary>
    /// <param name="outputDirectory">The export root. Use a directory under the host's exclusive control.</param>
    /// <param name="skills">The skill documents to export.</param>
    /// <returns>A validation diagnostic for invalid destinations, or null on success.</returns>
    /// <remarks>Root-level documents leave other entries in the root intact. File-system errors propagate to the caller.</remarks>
    public static A2uiDiagnostic? ExportToDirectory(string outputDirectory, IEnumerable<SkillFile> skills)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(skills);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new Dictionary<string, string>(comparer);
        var directories = new HashSet<string>(comparer);

        if (IsLink(root) || File.Exists(root))
            return InvalidDestination();

        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill);
            ArgumentNullException.ThrowIfNull(skill.Content);
            if (string.IsNullOrWhiteSpace(skill.FileName))
                return InvalidDestination();

            var segments = skill.FileName.Split(['/', '\\']);
            var path = root;
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment is "." or ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' ') ||
                    (OperatingSystem.IsWindows() && IsWindowsDeviceName(segment)))
                    return InvalidDestination();
                path = Path.Combine(path, segment);
                if (IsLink(path))
                    return InvalidDestination();
            }

            if (!files.TryAdd(path, skill.Content) || Directory.Exists(path))
                return InvalidDestination();

            for (var directory = Path.GetDirectoryName(path)!; !comparer.Equals(directory, root);
                directory = Path.GetDirectoryName(directory)!)
            {
                if (File.Exists(directory))
                    return InvalidDestination();
                directories.Add(directory);
            }
        }

        if (directories.Overlaps(files.Keys))
            return InvalidDestination();

        var ownedDirectories = files.Keys.Select(path => Path.GetDirectoryName(path)!)
            .Where(directory => !comparer.Equals(directory, root)).Distinct(comparer);
        foreach (var directory in ownedDirectories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        foreach (var (path, content) in files)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
            using (var temporaryFile = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                try
                {
                    using (var writer = new StreamWriter(temporaryFile))
                        writer.Write(content);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        return null;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
                name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(name[3]));
    }

    private static bool IsLink(string path) =>
        new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static A2uiDiagnostic InvalidDestination() =>
        new("ValidationError", "", "Each skill must have a distinct file path inside the output directory.");
}
