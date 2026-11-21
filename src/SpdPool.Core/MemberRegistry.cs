using System.Runtime.InteropServices;
using System.Text;

namespace SpdPool.Core;

/// <summary>
/// Probes pool members on disk. File placement is determined by which member's NTFS contains
/// the file, so no central metadata database is required.
/// </summary>
public sealed class MemberRegistry
{
    public const string DefaultMountRoot = @"C:\SpdPool\mnt";

    private static readonly (string Id, string Name)[] DefaultMembers =
    {
        ("m1", "m1"),
        ("m2", "m2"),
        ("m3", "m3"),
    };

    private readonly List<MemberInfo> _members;

    private MemberRegistry(List<MemberInfo> members) => _members = members;

    public IReadOnlyList<MemberInfo> Members => _members;
    public IReadOnlyList<MemberInfo> OnlineMembers => _members.Where(m => m.Online).ToList();

    public static MemberRegistry ProbeDefault(bool ssdOnM3 = true) =>
        Probe(DefaultMembers.Select(m => (m.Id, Path.Combine(DefaultMountRoot, m.Name), ssdOnM3 && m.Id == "m3")));

    public static MemberRegistry Probe(IEnumerable<(string Id, string Path, bool IsSsd)> members)
    {
        var list = new List<MemberInfo>();
        foreach (var (id, mountPath, isSsd) in members)
        {
            if (!Directory.Exists(mountPath))
            {
                list.Add(new MemberInfo
                {
                    Id = id, MountPath = mountPath, BackendPath = mountPath,
                    IsSsd = isSsd, Online = false, Error = "mount path missing",
                });
                continue;
            }
            try
            {
                var (total, free) = GetSpace(mountPath);
                // WinFsp cannot use a volume mount point as the backing root (Create "\" fails
                // with STATUS_OBJECT_PATH_NOT_FOUND). Resolve to the volume GUID path instead.
                var backend = ResolveVolumeGuid(mountPath) ?? mountPath;
                list.Add(new MemberInfo
                {
                    Id = id,
                    MountPath = mountPath,
                    BackendPath = backend,
                    TotalBytes = total,
                    FreeBytes = free,
                    IsSsd = isSsd,
                    Online = total > 0,
                    Error = total > 0 ? null : "GetDiskFreeSpaceEx failed",
                });
            }
            catch (Exception ex)
            {
                list.Add(new MemberInfo
                {
                    Id = id, MountPath = mountPath, BackendPath = mountPath,
                    IsSsd = isSsd, Online = false, Error = ex.Message,
                });
            }
        }
        return new MemberRegistry(list);
    }

    /// <summary>
    /// Choose the member for a new file: SSD first when it has room, otherwise the online member
    /// with the highest free percentage (M1 policy; refined with watermarks in M4).
    /// </summary>
    public MemberInfo? ChooseForNewFile()
    {
        var online = OnlineMembers;
        if (online.Count == 0) return null;
        var ssd = online.Where(m => m.IsSsd).OrderByDescending(m => m.FreePercent).FirstOrDefault();
        if (ssd != null) return ssd;
        return online.OrderByDescending(m => m.FreePercent).First();
    }

    public long TotalBytes => _members.Where(m => m.Online).Sum(m => m.TotalBytes);
    public long FreeBytes => _members.Where(m => m.Online).Sum(m => m.FreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        uint cchBufferLength);

    private static (long Total, long Free) GetSpace(string path)
    {
        if (GetDiskFreeSpaceEx(path, out _, out var total, out var free))
            return ((long)total, (long)free);
        return (0, 0);
    }

    private static string? ResolveVolumeGuid(string mountPath)
    {
        var point = mountPath.TrimEnd('\\') + "\\";
        var sb = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(point, sb, (uint)sb.Capacity) ? sb.ToString() : null;
    }
}
