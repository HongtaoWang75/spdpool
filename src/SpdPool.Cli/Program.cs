using SpdPool.Core;

namespace SpdPool.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (cmd)
        {
            case "probe":
            case "status":
                return PrintStatus();
            case "-h":
            case "--help":
            case "help":
                PrintUsage();
                return 0;
            default:
                Console.Error.WriteLine($"unknown command: {cmd}");
                PrintUsage();
                return 2;
        }
    }

    private static int PrintStatus()
    {
        var reg = MemberRegistry.ProbeDefault();
        Console.WriteLine($"members root: {MemberRegistry.DefaultMountRoot}");
        Console.WriteLine();
        Console.WriteLine($"{"ID",-4} {"Online",-6} {"SSD",-4} {"Total GB",10} {"Free GB",10} {"Used %",8}  Mount");
        foreach (var m in reg.Members)
        {
            Console.WriteLine($"{m.Id,-4} {(m.Online ? "yes" : "no"),-6} {(m.IsSsd ? "yes" : "no"),-4} " +
                              $"{m.TotalBytes / 1073741824.0,10:F1} {m.FreeBytes / 1073741824.0,10:F1} " +
                              $"{m.UsedPercent * 100,7:F1}%  {m.MountPath}" +
                              (m.Error != null ? $"  [{m.Error}]" : ""));
        }
        Console.WriteLine();
        Console.WriteLine($"pool total: {reg.TotalBytes / 1073741824.0:F1} GB, free: {reg.FreeBytes / 1073741824.0:F1} GB");
        var pick = reg.ChooseForNewFile();
        Console.WriteLine($"next file would go to: {(pick == null ? "(none)" : pick.Id)}");
        return reg.OnlineMembers.Count == 0 ? 1 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: spdpool <command>");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  probe     probe pool members and print capacity");
        Console.WriteLine("  status    same as probe");
    }
}
