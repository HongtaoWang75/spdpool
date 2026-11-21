namespace SpdPool.Fs;

/// <summary>
/// Background SSD tiering: when the SSD member exceeds the high watermark, migrate the coldest
/// files (lowest heat, oldest access) to the HDD member with the most free space.
/// Copy-then-delete; files that are open are skipped.
/// </summary>
internal sealed class Migrator
{
    private readonly List<MemberFs> _members;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _migratedBytes;

    public const int HighWaterPercent = 85;
    public const int LowWaterPercent = 75;

    public Migrator(IEnumerable<MemberFs> members)
    {
        _members = members.ToList();
        _thread = new Thread(Run) { IsBackground = true, Name = "SpdPool.Migrator" };
    }

    public void Start() => _thread.Start();
    public void Stop() => _stop = true;
    public long MigratedBytes => Interlocked.Read(ref _migratedBytes);

    private void Run()
    {
        while (!_stop)
        {
            try { Tick(); } catch { }
            for (int i = 0; i < 60 && !_stop; i++) Thread.Sleep(1000);
        }
    }

    private void Tick()
    {
        var ssd = _members.FirstOrDefault(m => m.IsSsd);
        if (ssd == null) return;
        if (UsagePercent(ssd) < HighWaterPercent) return;

        var hdds = _members.Where(m => !m.IsSsd).ToList();
        if (hdds.Count == 0) return;

        var root = ssd.Full("\\");
        var files = new List<(string Rel, string Full, long Size, long Heat, DateTime Access)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length == 0) continue;
                    var rel = f.Substring(root.Length - 1); // keep leading '\'
                    files.Add((rel, f, fi.Length, HeatTracker.Get(rel), fi.LastAccessTimeUtc));
                }
                catch { }
            }
        }
        catch { return; }

        files.Sort((a, b) => a.Heat != b.Heat ? a.Heat.CompareTo(b.Heat) : a.Access.CompareTo(b.Access));

        foreach (var f in files)
        {
            if (_stop || UsagePercent(ssd) < LowWaterPercent) break;
            var target = hdds.OrderByDescending(FreePercent).First();
            try
            {
                var dst = target.Full(f.Rel);
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dst)) continue;
                File.Copy(f.Full, dst, false);
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(f.Full));
                File.Delete(f.Full);
                Interlocked.Add(ref _migratedBytes, f.Size);
            }
            catch { /* open file or transient error: skip */ }
        }
    }

    private static double UsagePercent(MemberFs m)
    {
        try
        {
            if (GetDiskFreeSpaceEx(m.Full("\\"), out _, out var total, out var free) && total > 0)
                return (total - free) * 100.0 / total;
        }
        catch { }
        return 0;
    }

    private static double FreePercent(MemberFs m)
    {
        try
        {
            if (GetDiskFreeSpaceEx(m.Full("\\"), out var avail, out var total, out _) && total > 0)
                return avail * 100.0 / total;
        }
        catch { }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong avail, out ulong total, out ulong free);
}
