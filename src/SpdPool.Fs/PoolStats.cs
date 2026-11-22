using System.Collections.Concurrent;
using System.Text.Json;

namespace SpdPool.Fs;

/// <summary>Live pool statistics shared by the file system, migrator and the UI.</summary>
internal static class PoolStats
{
    public static long ReadBytes;
    public static long WriteBytes;
    public static long ReadOps;
    public static long WriteOps;
    public static int ActiveIo;

    public static volatile bool TaskActive;
    public static volatile string TaskText = "";

    public const string StatsPath = @"C:\ProgramData\SpdPool\stats.json";

    private static readonly ConcurrentDictionary<string, long[]> MemberIo = new();

    public static void OnRead(int n) { Interlocked.Add(ref ReadBytes, n); Interlocked.Increment(ref ReadOps); }
    public static void OnWrite(int n) { Interlocked.Add(ref WriteBytes, n); Interlocked.Increment(ref WriteOps); }

    public static void OnMemberRead(string id, int n)
    {
        var a = MemberIo.GetOrAdd(id, _ => new long[2]);
        Interlocked.Add(ref a[0], n);
    }

    public static void OnMemberWrite(string id, int n)
    {
        var a = MemberIo.GetOrAdd(id, _ => new long[2]);
        Interlocked.Add(ref a[1], n);
    }

    public static (long Read, long Write) GetMember(string id)
    {
        var a = MemberIo.GetOrAdd(id, _ => new long[2]);
        return (Interlocked.Read(ref a[0]), Interlocked.Read(ref a[1]));
    }

    public static void EnterIo() => Interlocked.Increment(ref ActiveIo);
    public static void ExitIo() => Interlocked.Decrement(ref ActiveIo);
}

/// <summary>Writes a 1-second snapshot of PoolStats to stats.json for the UI.</summary>
internal sealed class StatsWriter
{
    private readonly Thread _thread;
    private readonly string[] _memberIds;
    private readonly Dictionary<string, (long R, long W)> _prevMember = new();
    private volatile bool _stop;

    public StatsWriter(IEnumerable<string> memberIds)
    {
        _memberIds = memberIds.ToArray();
        _thread = new Thread(Run) { IsBackground = true, Name = "SpdPool.Stats" };
    }

    public void Start() => _thread.Start();
    public void Stop() => _stop = true;

    private void Run()
    {
        long prevR = 0, prevW = 0, prevRo = 0, prevWo = 0;
        try { Directory.CreateDirectory(Path.GetDirectoryName(PoolStats.StatsPath)!); } catch { }

        while (!_stop)
        {
            Thread.Sleep(1000);
            try
            {
                var r = Interlocked.Read(ref PoolStats.ReadBytes);
                var w = Interlocked.Read(ref PoolStats.WriteBytes);
                var ro = Interlocked.Read(ref PoolStats.ReadOps);
                var wo = Interlocked.Read(ref PoolStats.WriteOps);

                var members = new Dictionary<string, object>();
                foreach (var id in _memberIds)
                {
                    var (mr, mw) = PoolStats.GetMember(id);
                    var pr = _prevMember.TryGetValue(id, out var pm) ? pm : (R: 0L, W: 0L);
                    members[id] = new
                    {
                        readBytesPerSec = Math.Max(0, mr - pr.R),
                        writeBytesPerSec = Math.Max(0, mw - pr.W),
                    };
                    _prevMember[id] = (mr, mw);
                }

                var snap = new
                {
                    timestamp = DateTime.Now.ToString("O"),
                    readBytesPerSec = Math.Max(0, r - prevR),
                    writeBytesPerSec = Math.Max(0, w - prevW),
                    readOpsPerSec = Math.Max(0, ro - prevRo),
                    writeOpsPerSec = Math.Max(0, wo - prevWo),
                    queueDepth = PoolStats.ActiveIo,
                    taskActive = PoolStats.TaskActive,
                    task = PoolStats.TaskText,
                    members,
                };
                prevR = r; prevW = w; prevRo = ro; prevWo = wo;
                File.WriteAllText(PoolStats.StatsPath, JsonSerializer.Serialize(snap));
            }
            catch { }
        }
    }
}
