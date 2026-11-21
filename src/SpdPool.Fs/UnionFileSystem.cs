// Union file system: merges N NTFS member volumes into a single S: namespace.
// Each file lives entirely on one member (file-level placement); directories are merged
// across members for enumeration.

using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;

using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;

namespace SpdPool.Fs;

internal sealed class MemberFs
{
    public required string Id { get; init; }
    public required string Root { get; init; }        // backend path, ends with '\'
    public required string MountPath { get; init; }   // C:\SpdPool\mnt\m1
    public bool IsSsd { get; init; }

    public string Full(string rel) => Root + rel.TrimStart('\\');
    public long FreeBytes()
    {
        try
        {
            if (GetDiskFreeSpaceEx(Root, out _, out var total, out var free))
                return (long)(free * 100 / Math.Max(1UL, total));
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong avail, out ulong total, out ulong free);
}

internal sealed class UnionDesc
{
    public MemberFs Member;
    public string RelPath = "\\";
    public FileStream Stream;
    public FileSystemInfo[] Entries;
    public bool IsDir;
}

internal sealed class UnionFileSystem : FileSystemBase
{
    private const int ALLOCATION_UNIT = 4096;
    private readonly List<MemberFs> _members;
    private readonly object _lock = new();

    public UnionFileSystem(IEnumerable<MemberFs> members) => _members = members.ToList();

    private static void ThrowWin32(int error) =>
        throw new IOException(null, unchecked((int)(0x80070000 | error)));

    private static void ThrowNt(int status) => ThrowWin32((int)Win32FromNtStatus(status));

    private static void FillInfo(FileSystemInfo info, out FileInfo fi)
    {
        fi.FileAttributes = (uint)info.Attributes;
        fi.ReparseTag = 0;
        fi.FileSize = info is System.IO.FileInfo f ? (ulong)f.Length : 0;
        fi.AllocationSize = (fi.FileSize + ALLOCATION_UNIT - 1) / ALLOCATION_UNIT * ALLOCATION_UNIT;
        fi.CreationTime = (ulong)info.CreationTimeUtc.ToFileTimeUtc();
        fi.LastAccessTime = (ulong)info.LastAccessTimeUtc.ToFileTimeUtc();
        fi.LastWriteTime = (ulong)info.LastWriteTimeUtc.ToFileTimeUtc();
        fi.ChangeTime = fi.LastWriteTime;
        fi.IndexNumber = 0;
        fi.HardLinks = 0;
    }

    private int GetInfo(UnionDesc d, out FileInfo fi)
    {
        if (d.Stream != null)
        {
            var path = d.Stream.Name;
            fi.FileAttributes = (uint)System.IO.File.GetAttributes(path);
            fi.ReparseTag = 0;
            fi.FileSize = (ulong)d.Stream.Length;
            fi.AllocationSize = (fi.FileSize + ALLOCATION_UNIT - 1) / ALLOCATION_UNIT * ALLOCATION_UNIT;
            fi.CreationTime = (ulong)File.GetCreationTimeUtc(path).ToFileTimeUtc();
            fi.LastAccessTime = (ulong)File.GetLastAccessTimeUtc(path).ToFileTimeUtc();
            fi.LastWriteTime = (ulong)File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
            fi.ChangeTime = fi.LastWriteTime;
            fi.IndexNumber = 0;
            fi.HardLinks = 0;
            return STATUS_SUCCESS;
        }
        FillInfo(new DirectoryInfo(d.Member.Full(d.RelPath)), out fi);
        return STATUS_SUCCESS;
    }

    /// <summary>Find the member that owns this path. Returns null if absent everywhere.</summary>
    private UnionDesc Find(string rel)
    {
        foreach (var m in _members)
        {
            var p = m.Full(rel);
            if (Directory.Exists(p))
                return new UnionDesc { Member = m, RelPath = rel, IsDir = true };
            if (System.IO.File.Exists(p))
                return new UnionDesc { Member = m, RelPath = rel, IsDir = false };
        }
        return null;
    }

    private static void EnsureParent(MemberFs m, string rel)
    {
        var idx = rel.LastIndexOf('\\');
        if (idx <= 0) return;
        var parent = rel.Substring(0, idx);
        var p = m.Full(parent);
        if (!Directory.Exists(p)) Directory.CreateDirectory(p);
    }

    private MemberFs ChooseMember()
    {
        var ssd = _members.Where(m => m.IsSsd).OrderByDescending(m => m.FreeBytes()).FirstOrDefault();
        return ssd ?? _members.OrderByDescending(m => m.FreeBytes()).First();
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = ALLOCATION_UNIT;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = true;
        host.FlushAndPurgeOnCleanup = true;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0;
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex)
    {
        var hr = ex.HResult;
        if ((hr & 0xFFFF0000) == unchecked((int)0x80070000))
            return NtStatusFromWin32((uint)hr & 0xFFFF);
        return STATUS_UNEXPECTED_IO_ERROR;
    }

    public override int GetVolumeInfo(out VolumeInfo vi)
    {
        vi = default;
        long total = 0, free = 0;
        foreach (var m in _members)
        {
            var p = m.Full("\\");
            if (GetDiskFreeSpaceEx(p, out var avail, out var t, out var f))
            {
                total += (long)t;
                free += (long)f;
            }
        }
        vi.TotalSize = (ulong)Math.Max(0, total);
        vi.FreeSize = (ulong)Math.Max(0, free);
        return STATUS_SUCCESS;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong avail, out ulong total, out ulong free);

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var d = Find(fileName);
        if (d == null) { fileAttributes = 0; return STATUS_OBJECT_NAME_NOT_FOUND; }
        var p = d.Member.Full(fileName);
        fileAttributes = (uint)System.IO.File.GetAttributes(p);
        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fi, out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        normalizedName = null;
        fi = default;
        var isDir = 0 != (createOptions & FILE_DIRECTORY_FILE);
        var rel = fileName;

        try
        {
            if (isDir)
            {
                // Create the directory on the chosen member only. A single copy keeps deletion
                // simple; the union still merges same-named directories if they exist elsewhere.
                var dirMember = ChooseMember();
                EnsureParent(dirMember, rel);
                var dirPath = dirMember.Full(rel);
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                var d = new UnionDesc { Member = dirMember, RelPath = rel, IsDir = true };
                fileDesc = d;
                return GetInfo(d, out fi);
            }

            var existing = Find(rel);
            if (existing != null) throw new IOException(null, unchecked((int)0x80070050)); // FILE_EXISTS

            var target = ChooseMember();
            EnsureParent(target, rel);
            var path = target.Full(rel);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Write | FileShare.Delete, ALLOCATION_UNIT, FileOptions.None);
            if (fileAttributes != 0)
                System.IO.File.SetAttributes(path, (FileAttributes)fileAttributes | System.IO.FileAttributes.Archive);
            var desc = new UnionDesc { Member = target, RelPath = rel, Stream = stream };
            fileDesc = desc;
            return GetInfo(desc, out fi);
        }
        catch (Exception ex)
        {
            return ExceptionHandler(ex);
        }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fi, out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        normalizedName = null;
        fi = default;

        try
        {
            var d = Find(fileName);
            if (d == null) return STATUS_OBJECT_NAME_NOT_FOUND;
            if (!d.IsDir)
            {
                d.Stream = new FileStream(d.Member.Full(fileName), FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Write | FileShare.Delete, ALLOCATION_UNIT, FileOptions.None);
            }
            fileDesc = d;
            return GetInfo(d, out fi);
        }
        catch (Exception ex)
        {
            return ExceptionHandler(ex);
        }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var d = (UnionDesc)fileDesc;
        if (0 != (flags & CleanupDelete))
        {
            TryDelete(d);
            d.Stream?.Dispose();
            d.Stream = null;
        }
    }

    private void TryDelete(UnionDesc d)
    {
        try
        {
            if (d.IsDir)
            {
                // Remove every copy of the directory across members (recursively: the user
                // asked to delete the whole tree, and delete-on-close may leave it non-empty).
                foreach (var m in _members)
                {
                    var p = m.Full(d.RelPath);
                    try
                    {
                        if (Directory.Exists(p)) Directory.Delete(p, true);
                    }
                    catch { }
                }
            }
            else
            {
                d.Stream?.Dispose();
                d.Stream = null;
                var p = d.Member.Full(d.RelPath);
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            }
        }
        catch { }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var d = (UnionDesc)fileDesc;
        d?.Stream?.Dispose();
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        var d = (UnionDesc)fileDesc;
        if (d.Stream == null || offset >= (ulong)d.Stream.Length)
        {
            bytesTransferred = 0;
            return STATUS_END_OF_FILE;
        }
        var bytes = new byte[length];
        d.Stream.Seek((long)offset, SeekOrigin.Begin);
        var read = d.Stream.Read(bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, buffer, read);
        bytesTransferred = (uint)read;
        HeatTracker.Touch(d.RelPath);
        return STATUS_SUCCESS;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fi)
    {
        var d = (UnionDesc)fileDesc;
        if (constrainedIo)
        {
            if (offset >= (ulong)d.Stream.Length)
            {
                bytesTransferred = 0;
                fi = default;
                return STATUS_SUCCESS;
            }
            if (offset + length > (ulong)d.Stream.Length)
                length = (uint)((ulong)d.Stream.Length - offset);
        }
        var bytes = new byte[length];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        if (!writeToEndOfFile) d.Stream.Seek((long)offset, SeekOrigin.Begin);
        d.Stream.Write(bytes, 0, bytes.Length);
        bytesTransferred = (uint)bytes.Length;
        HeatTracker.Touch(d.RelPath);
        return GetInfo(d, out fi);
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fi)
    {
        var d = (UnionDesc)fileDesc;
        if (d?.Stream == null) { fi = default; return STATUS_SUCCESS; }
        d.Stream.Flush(true);
        return GetInfo(d, out fi);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fi)
        => GetInfo((UnionDesc)fileDesc, out fi);

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fi)
    {
        var d = (UnionDesc)fileDesc;
        var path = d.Member.Full(d.RelPath);
        try
        {
            if (fileAttributes != 0 && fileAttributes != uint.MaxValue)
                System.IO.File.SetAttributes(path, (FileAttributes)fileAttributes);
            if (creationTime != 0) System.IO.File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc((long)creationTime));
            if (lastAccessTime != 0) System.IO.File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc((long)lastAccessTime));
            if (lastWriteTime != 0) System.IO.File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc((long)lastWriteTime));
        }
        catch { }
        return GetInfo(d, out fi);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize,
        bool setAllocationSize, out FileInfo fi)
    {
        var d = (UnionDesc)fileDesc;
        if (d.Stream != null && (!setAllocationSize || (ulong)d.Stream.Length > newSize))
            d.Stream.SetLength((long)newSize);
        return GetInfo(d, out fi);
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
        => STATUS_SUCCESS;

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName,
        bool replaceIfExists)
    {
        var src = Find(fileName);
        if (src == null) return STATUS_OBJECT_NAME_NOT_FOUND;
        var dst = Find(newFileName);

        // Same member: cheap move.
        if (dst != null && dst.Member != src.Member)
            return STATUS_NOT_SAME_DEVICE;

        if (src.IsDir)
        {
            // Move the directory on every member that has it.
            var moved = false;
            foreach (var m in _members)
            {
                var from = m.Full(fileName);
                if (Directory.Exists(from))
                {
                    var to = m.Full(newFileName);
                    if (replaceIfExists && Directory.Exists(to)) Directory.Delete(to, true);
                    Directory.Move(from, to);
                    moved = true;
                }
            }
            return moved ? STATUS_SUCCESS : STATUS_OBJECT_NAME_NOT_FOUND;
        }

        var srcPath = src.Member.Full(fileName);
        var dstPath = src.Member.Full(newFileName);
        if (replaceIfExists && System.IO.File.Exists(dstPath)) System.IO.File.Delete(dstPath);
        EnsureParent(src.Member, newFileName);
        System.IO.File.Move(srcPath, dstPath);
        return STATUS_SUCCESS;
    }

    private sealed class EntryComparer : IComparer
    {
        public int Compare(object x, object y) =>
            string.Compare((string)((DictionaryEntry)x).Key, (string)((DictionaryEntry)y).Key,
                StringComparison.OrdinalIgnoreCase);
    }

    private static readonly EntryComparer Comparer = new();

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fi)
    {
        var d = (UnionDesc)fileDesc;
        if (d.Entries == null)
        {
            if (pattern != null)
                pattern = pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
            else
                pattern = "*";

            var merged = new SortedList(StringComparer.OrdinalIgnoreCase);
            if (d.RelPath != "\\")
                merged.Add(".", new DirectoryInfo(d.Member.Full(d.RelPath)));

            foreach (var m in _members)
            {
                var dir = m.Full(d.RelPath);
                if (!Directory.Exists(dir)) continue;
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern))
                    if (!merged.ContainsKey(info.Name))
                        merged.Add(info.Name, info);
            }

            d.Entries = new FileSystemInfo[merged.Count];
            for (int i = 0; i < merged.Count; i++)
                d.Entries[i] = (FileSystemInfo)merged.GetByIndex(i);
        }

        int index;
        if (context == null)
        {
            index = 0;
            if (marker != null)
            {
                var arr = new DictionaryEntry[d.Entries.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = new DictionaryEntry(d.Entries[i].Name, d.Entries[i]);
                index = Array.BinarySearch(arr, new DictionaryEntry(marker, null), Comparer);
                index = index >= 0 ? index + 1 : ~index;
            }
        }
        else index = (int)context;

        if (index < d.Entries.Length)
        {
            context = index + 1;
            fileName = d.Entries[index].Name;
            FillInfo(d.Entries[index], out fi);
            return true;
        }

        fileName = null;
        fi = default;
        return false;
    }
}

/// <summary>Simple access-heat tracker used by the tiering migrator (M4).</summary>
internal static class HeatTracker
{
    private static readonly Dictionary<string, long> _counts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static void Touch(string rel)
    {
        lock (_lock)
        {
            _counts.TryGetValue(rel, out var n);
            _counts[rel] = n + 1;
        }
    }

    public static long Get(string rel)
    {
        lock (_lock) return _counts.TryGetValue(rel, out var n) ? n : 0;
    }

    public static List<(string Path, long Count)> Snapshot()
    {
        lock (_lock) return _counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
