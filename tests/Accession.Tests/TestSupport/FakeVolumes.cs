using Accession.Core.Inventories;

namespace Accession.Tests.TestSupport;

/// <summary>
/// Drives for tests: each folder directly inside <c>drivesFolder</c> stands for a drive root (<c>…\E</c> for E:\).
/// A mounted one has a serial; an unmounted one is a drive that is not connected.
/// </summary>
public sealed class FakeVolumes(string drivesFolder) : IVolumeProbe
{
    private readonly Dictionary<string, VolumeId> _mounted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the drive's folder and gives it a serial.</summary>
    public string Mount(string name, string serial, string label = "USB")
    {
        var root = Path.Combine(drivesFolder, name);
        Directory.CreateDirectory(root);
        _mounted[name] = new VolumeId(serial, label);
        return root;
    }

    public void Unmount(string name) => _mounted.Remove(name);

    public VolumeId? Identify(string path) => DriveName(path) is { } name && _mounted.TryGetValue(name, out var id) ? id : null;

    public IReadOnlyList<string> LocalDriveRoots() => [.. _mounted.Keys.Select(name => Path.Combine(drivesFolder, name))];

    public string PathOnDrive(string path)
    {
        var parts = Relative(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? string.Empty : Path.Combine(parts[1..]);
    }

    public string ToStablePath(string path) => Path.GetFullPath(path);

    private string? DriveName(string path)
    {
        var relative = Relative(path);
        return relative.StartsWith("..", StringComparison.Ordinal) || relative.Length == 0
            ? null
            : relative.Split(Path.DirectorySeparatorChar)[0];
    }

    private string Relative(string path) => Path.GetRelativePath(drivesFolder, Path.GetFullPath(path));
}
