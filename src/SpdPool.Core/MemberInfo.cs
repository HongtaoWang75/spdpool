namespace SpdPool.Core;

/// <summary>One physical member of the pool (a NTFS volume, mounted on a hidden directory).</summary>
public sealed class MemberInfo
{
    public required string Id { get; init; }          // m1 / m2 / m3
    public required string MountPath { get; init; }   // C:\SpdPool\mnt\m1
    public required string BackendPath { get; init; } // \\?\Volume{...}\ or drive letter path
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsSsd { get; init; }
    public bool Online { get; init; }
    public string? Error { get; init; }

    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 1.0;
    public double FreePercent => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0.0;
}
