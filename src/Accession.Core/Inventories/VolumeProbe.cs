using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Accession.Core.Inventories;

/// <summary>A local drive as Windows identifies it: its serial number (<c>1A2B-3C4D</c>) and label.</summary>
public sealed record VolumeId(string Serial, string Label);

/// <summary>
/// Drive facts for media outside the root. Drive letters change (a USB drive is E: today, F: tomorrow, and E: on
/// another computer is another device), so a media on a local drive is recognised by the drive's serial number.
/// </summary>
public interface IVolumeProbe
{
    /// <summary>The local fixed or removable drive holding <paramref name="path"/>; null for network paths or when unknown.</summary>
    VolumeId? Identify(string path);

    /// <summary>Roots of the local drives now connected (<c>C:\</c>, <c>E:\</c>…): where a drive is looked for by its serial.</summary>
    IReadOnlyList<string> LocalDriveRoots();

    /// <summary>The part of a path after its drive root (<c>E:\Custodian\Mail</c> → <c>Custodian\Mail</c>); "" for the root itself.</summary>
    string PathOnDrive(string path);

    /// <summary>A path on a mapped network drive as its UNC path (<c>Z:\x</c> → <c>\\srv\share\x</c>): mapped letters differ per user.</summary>
    string ToStablePath(string path);
}

/// <summary>The drives of this computer (Windows); elsewhere nothing is known and paths stay as they are.</summary>
public sealed class SystemVolumeProbe : IVolumeProbe
{
    public static readonly SystemVolumeProbe Instance = new();

    public VolumeId? Identify(string path)
    {
        if (!OperatingSystem.IsWindows() || LocalDriveRoot(path) is not { } root)
        {
            return null;
        }

        var label = new StringBuilder(261);
        if (!GetVolumeInformation(root, label, label.Capacity, out var serial, out _, out _, null, 0))
        {
            return null; // e.g. an empty card reader slot
        }

        return new VolumeId(FormatSerial(serial), label.ToString());
    }

    public IReadOnlyList<string> LocalDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return [.. DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public string PathOnDrive(string path)
    {
        var full = Path.GetFullPath(path);
        return full[(Path.GetPathRoot(full)?.Length ?? 0)..].TrimEnd('\\', '/');
    }

    public string ToStablePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || full.Length < 2 || full[1] != ':')
        {
            return full;
        }

        var drive = full[..2];
        var remote = new StringBuilder(1024);
        var length = remote.Capacity;
        return WNetGetConnection(drive, remote, ref length) == 0 && remote.Length > 0
            ? remote.ToString().TrimEnd('\\') + full[2..]
            : full;
    }

    /// <summary><c>1A2B-3C4D</c>, as <c>vol</c> and Explorer show it.</summary>
    public static string FormatSerial(uint serial) =>
        string.Create(CultureInfo.InvariantCulture, $"{serial >> 16:X4}-{serial & 0xFFFF:X4}");

    /// <summary><c>E:\</c> for a path on a local drive; null for UNC paths and network drives.</summary>
    private static string? LocalDriveRoot(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full) is not { Length: > 0 } root)
        {
            return null;
        }

        try
        {
            return new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
