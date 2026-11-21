using System.Management;
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

    private sealed class PhysicalDisk
    {
        public string? DeviceId;
        public string? Name;
        public int Health;          // 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown
        public long Size;
        public int? Temperature;
    }

    private readonly List<MemberInfo> _members;

    private MemberRegistry(List<MemberInfo> members) => _members = members;

    public IReadOnlyList<MemberInfo> Members => _members;
    public IReadOnlyList<MemberInfo> OnlineMembers => _members.Where(m => m.Online).ToList();

    public static MemberRegistry ProbeDefault(bool ssdOnM3 = true) =>
        Probe(DefaultMembers.Select(m => (m.Id, Path.Combine(DefaultMountRoot, m.Name), ssdOnM3 && m.Id == "m3")));

    public static MemberRegistry Probe(IEnumerable<(string Id, string Path, bool IsSsd)> members)
    {
        var probes = new List<(string Id, string Mount, string Backend, long Total, long Free, bool IsSsd, string? Error)>();
        foreach (var (id, mountPath, isSsd) in members)
        {
            if (!Directory.Exists(mountPath))
            {
                probes.Add((id, mountPath, mountPath, 0, 0, isSsd, "mount path missing"));
                continue;
            }
            try
            {
                var (total, free) = GetSpace(mountPath);
                var backend = ResolveVolumeGuid(mountPath) ?? mountPath;
                probes.Add((id, mountPath, backend, total, free, isSsd, total > 0 ? null : "GetDiskFreeSpaceEx failed"));
            }
            catch (Exception ex)
            {
                probes.Add((id, mountPath, mountPath, 0, 0, isSsd, ex.Message));
            }
        }

        // Match members to physical disks by size (largest first), then attach name/health/temp.
        var disks = QueryPhysicalDisks().OrderByDescending(d => d.Size).ToList();
        var used = new HashSet<PhysicalDisk>();
        var list = new List<MemberInfo>();

        foreach (var p in probes.OrderByDescending(x => x.Total))
        {
            PhysicalDisk? disk = null;
            if (p.Total > 0)
            {
                disk = disks.FirstOrDefault(d => !used.Contains(d) && d.Size >= p.Total && d.Size <= p.Total * 1.15);
                if (disk != null) used.Add(disk);
            }
            list.Add(new MemberInfo
            {
                Id = p.Id,
                MountPath = p.Mount,
                BackendPath = p.Backend,
                TotalBytes = p.Total,
                FreeBytes = p.Free,
                IsSsd = p.IsSsd,
                Online = p.Total > 0,
                Error = p.Error,
                DiskName = disk?.Name,
                DiskHealth = disk == null ? null : HealthText(disk.Health),
                TemperatureCelsius = disk?.Temperature,
            });
        }

        return new MemberRegistry(list.OrderBy(m => m.Id).ToList());
    }

    /// <summary>
    /// Choose the member for a new file: SSD first when it has room, otherwise the online member
    /// with the highest free percentage.
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

    private static string HealthText(int health) => health switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    private static List<PhysicalDisk> QueryPhysicalDisks()
    {
        var list = new List<PhysicalDisk>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\Microsoft\Windows\Storage"),
                new ObjectQuery("SELECT DeviceId,FriendlyName,HealthStatus,Size FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var devId = mo["DeviceId"]?.ToString();
                list.Add(new PhysicalDisk
                {
                    DeviceId = devId,
                    Name = mo["FriendlyName"]?.ToString(),
                    Health = mo["HealthStatus"] is ushort h ? h : 5,
                    Size = Convert.ToInt64(mo["Size"] ?? 0L),
                    Temperature = int.TryParse(devId, out var dn) ? QueryTemperature(dn) : null,
                });
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\Microsoft\Windows\Storage"),
                new ObjectQuery("SELECT DeviceId,Temperature FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["DeviceId"]?.ToString();
                var temp = mo["Temperature"];
                if (id == null || temp == null) continue;
                var d = list.FirstOrDefault(x => x.DeviceId == id);
                if (d != null)
                {
                    var t = Convert.ToInt32(temp);
                    if (t > 0 && t < 120) d.Temperature = t;
                }
            }
        }
        catch { }

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public uint AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_TEMPERATURE_DATA_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public ushort CriticalTemperature;
        public ushort WarningTemperature;
        public ushort InfoCount;
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_TEMPERATURE_INFO
    {
        public ushort Index;
        public ushort Temperature;
        public ushort OverTemperature;
        public ushort Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    /// <summary>Windows 10+ storage temperature IOCTL (no smartctl required).</summary>
    private static int? QueryTemperature(int diskNumber)
    {
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try
        {
            using var h = File.Open($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 49 /* StorageDeviceTemperatureProperty */ };
            inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<STORAGE_PROPERTY_QUERY>());
            outBuf = Marshal.AllocHGlobal(1024);
            Marshal.StructureToPtr(query, inBuf, false);
            if (!DeviceIoControl(h.SafeFileHandle.DangerousGetHandle(), 0x002D1400,
                    inBuf, (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), outBuf, 1024, out _, IntPtr.Zero))
                return null;
            var desc = Marshal.PtrToStructure<STORAGE_TEMPERATURE_DATA_DESCRIPTOR>(outBuf);
            if (desc.InfoCount == 0) return null;
            var info = Marshal.PtrToStructure<STORAGE_TEMPERATURE_INFO>(
                IntPtr.Add(outBuf, Marshal.SizeOf<STORAGE_TEMPERATURE_DATA_DESCRIPTOR>()));
            return info.Temperature is > 0 and < 120 ? info.Temperature : null;
        }
        catch { return null; }
        finally
        {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
        }
    }

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
