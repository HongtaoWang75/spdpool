// M0 feasibility probe: minimal .NET 8 passthrough file system on WinFsp.
// Based on the official passthrough-dotnet sample (GPLv3-compatible usage).

using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

using Fsp;
using SpdPool.Core;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;

namespace SpdPool.Fs;

internal sealed class PassthroughFs : FileSystemBase
{
    private const int ALLOCATION_UNIT = 4096;
    private readonly string _root;

    public PassthroughFs(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
    }

    private string Full(string name) => _root + name;

    private static void ThrowWin32(int error) =>
        throw new IOException(null, unchecked((int)(0x80070000 | error)));

    private static void ThrowNtStatus(int status) =>
        ThrowWin32((int)Win32FromNtStatus(status));

    private sealed class Desc
    {
        public FileStream Stream;
        public DirectoryInfo Dir;
        public DictionaryEntry[] Entries;

        public static void Fill(FileSystemInfo info, out FileInfo fi)
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

        public int GetFileInfo(out FileInfo fi)
        {
            if (Stream != null)
            {
                fi.FileAttributes = (uint)System.IO.File.GetAttributes(Stream.Name);
                fi.ReparseTag = 0;
                fi.FileSize = (ulong)Stream.Length;
                fi.AllocationSize = (fi.FileSize + ALLOCATION_UNIT - 1) / ALLOCATION_UNIT * ALLOCATION_UNIT;
                fi.CreationTime = (ulong)File.GetCreationTimeUtc(Stream.Name).ToFileTimeUtc();
                fi.LastAccessTime = (ulong)File.GetLastAccessTimeUtc(Stream.Name).ToFileTimeUtc();
                fi.LastWriteTime = (ulong)File.GetLastWriteTimeUtc(Stream.Name).ToFileTimeUtc();
                fi.ChangeTime = fi.LastWriteTime;
                fi.IndexNumber = 0;
                fi.HardLinks = 0;
            }
            else
            {
                Fill(Dir, out fi);
            }
            return STATUS_SUCCESS;
        }

        public void SetBasicInfo(uint attributes, ulong creation, ulong access, ulong write)
        {
            if (attributes != 0 && attributes != uint.MaxValue)
            {
                if (Stream != null) System.IO.File.SetAttributes(Stream.Name, (FileAttributes)attributes);
                else Dir.Attributes = (System.IO.FileAttributes)attributes;
            }
            if (creation != 0)
            {
                var dt = DateTime.FromFileTimeUtc((long)creation);
                if (Stream != null) System.IO.File.SetCreationTimeUtc(Stream.Name, dt);
                else Dir.CreationTimeUtc = dt;
            }
            if (access != 0)
            {
                var dt = DateTime.FromFileTimeUtc((long)access);
                if (Stream != null) System.IO.File.SetLastAccessTimeUtc(Stream.Name, dt);
                else Dir.LastAccessTimeUtc = dt;
            }
            if (write != 0)
            {
                var dt = DateTime.FromFileTimeUtc((long)write);
                if (Stream != null) System.IO.File.SetLastWriteTimeUtc(Stream.Name, dt);
                else Dir.LastWriteTimeUtc = dt;
            }
        }

        public void SetDisposition(bool safe)
        {
            try
            {
                if (Stream != null) System.IO.File.Delete(Stream.Name);
                else Dir.Delete();
            }
            catch (Exception ex)
            {
                if (!safe) throw new IOException(ex.Message, ex.HResult);
            }
        }
    }

    private sealed class EntryComparer : IComparer
    {
        public int Compare(object x, object y) =>
            string.Compare((string)((DictionaryEntry)x).Key, (string)((DictionaryEntry)y).Key, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly EntryComparer Comparer = new();

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
        try { host.VolumeCreationTime = (ulong)Directory.GetCreationTimeUtc(_root).ToFileTimeUtc(); }
        catch { host.VolumeCreationTime = 0; }
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
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_root));
            vi.TotalSize = (ulong)drive.TotalSize;
            vi.FreeSize = (ulong)drive.TotalFreeSpace;
        }
        catch { }
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var path = Full(fileName);
        if (System.IO.File.Exists(path) || Directory.Exists(path))
        {
            fileAttributes = (uint)System.IO.File.GetAttributes(path);
            return STATUS_SUCCESS;
        }
        fileAttributes = 0;
        return STATUS_OBJECT_NAME_NOT_FOUND;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fi, out string normalizedName)
    {
        var path = Full(fileName);
        Desc desc;
        if (0 == (createOptions & FILE_DIRECTORY_FILE))
        {
            desc = new Desc { Stream = new FileStream(path, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read | FileShare.Write | FileShare.Delete,
                ALLOCATION_UNIT, FileOptions.None) };
            if (fileAttributes != 0)
                System.IO.File.SetAttributes(path, (FileAttributes)fileAttributes | System.IO.FileAttributes.Archive);
        }
        else
        {
            if (Directory.Exists(path)) ThrowNtStatus(STATUS_OBJECT_NAME_COLLISION);
            desc = new Desc { Dir = Directory.CreateDirectory(path) };
            if (fileAttributes != 0) desc.Dir.Attributes = (System.IO.FileAttributes)fileAttributes;
        }
        fileNode = null;
        fileDesc = desc;
        normalizedName = null;
        return desc.GetFileInfo(out fi);
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fi, out string normalizedName)
    {
        var path = Full(fileName);
        fileNode = null;
        fileDesc = null;
        normalizedName = null;
        fi = default;

        Desc desc;
        if (Directory.Exists(path))
        {
            desc = new Desc { Dir = new DirectoryInfo(path) };
        }
        else if (System.IO.File.Exists(path))
        {
            desc = new Desc { Stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Write | FileShare.Delete, ALLOCATION_UNIT, FileOptions.None) };
        }
        else
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        fileDesc = desc;
        return desc.GetFileInfo(out fi);
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var desc = (Desc)fileDesc;
        if (0 != (flags & CleanupDelete))
        {
            desc.SetDisposition(true);
            desc.Stream?.Dispose();
            desc.Stream = null;
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var desc = (Desc)fileDesc;
        desc?.Stream?.Dispose();
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        var desc = (Desc)fileDesc;
        if (desc.Stream == null || offset >= (ulong)desc.Stream.Length)
        {
            bytesTransferred = 0;
            return STATUS_END_OF_FILE;
        }
        var bytes = new byte[length];
        desc.Stream.Seek((long)offset, SeekOrigin.Begin);
        var read = desc.Stream.Read(bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, buffer, read);
        bytesTransferred = (uint)read;
        return STATUS_SUCCESS;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fi)
    {
        var desc = (Desc)fileDesc;
        if (constrainedIo)
        {
            if (offset >= (ulong)desc.Stream.Length)
            {
                bytesTransferred = 0;
                fi = default;
                return STATUS_SUCCESS;
            }
            if (offset + length > (ulong)desc.Stream.Length)
                length = (uint)((ulong)desc.Stream.Length - offset);
        }
        var bytes = new byte[length];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        if (!writeToEndOfFile) desc.Stream.Seek((long)offset, SeekOrigin.Begin);
        desc.Stream.Write(bytes, 0, bytes.Length);
        bytesTransferred = (uint)bytes.Length;
        return desc.GetFileInfo(out fi);
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fi)
    {
        var desc = (Desc)fileDesc;
        if (desc?.Stream == null) { fi = default; return STATUS_SUCCESS; }
        desc.Stream.Flush(true);
        return desc.GetFileInfo(out fi);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fi)
        => ((Desc)fileDesc).GetFileInfo(out fi);

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fi)
    {
        var desc = (Desc)fileDesc;
        desc.SetBasicInfo(fileAttributes, creationTime, lastAccessTime, lastWriteTime);
        return desc.GetFileInfo(out fi);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize,
        bool setAllocationSize, out FileInfo fi)
    {
        var desc = (Desc)fileDesc;
        if (desc.Stream != null && (!setAllocationSize || (ulong)desc.Stream.Length > newSize))
            desc.Stream.SetLength((long)newSize);
        return desc.GetFileInfo(out fi);
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        ((Desc)fileDesc).SetDisposition(false);
        return STATUS_SUCCESS;
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName,
        bool replaceIfExists)
    {
        var from = Full(fileName);
        var to = Full(newFileName);
        if (replaceIfExists)
        {
            if (System.IO.File.Exists(to)) System.IO.File.Delete(to);
        }
        System.IO.File.Move(from, to);
        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fi)
    {
        var desc = (Desc)fileDesc;
        if (desc.Entries == null)
        {
            if (pattern != null)
                pattern = pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
            else
                pattern = "*";

            var list = new SortedList();
            if (desc.Dir.Parent != null)
            {
                list.Add(".", desc.Dir);
                list.Add("..", desc.Dir.Parent);
            }
            foreach (var info in desc.Dir.EnumerateFileSystemInfos(pattern))
                list.Add(info.Name, info);

            desc.Entries = new DictionaryEntry[list.Count];
            list.CopyTo(desc.Entries, 0);
        }

        int index;
        if (context == null)
        {
            index = 0;
            if (marker != null)
            {
                index = Array.BinarySearch(desc.Entries, new DictionaryEntry(marker, null), Comparer);
                index = index >= 0 ? index + 1 : ~index;
            }
        }
        else index = (int)context;

        if (index < desc.Entries.Length)
        {
            context = index + 1;
            fileName = (string)desc.Entries[index].Key;
            Desc.Fill((FileSystemInfo)desc.Entries[index].Value, out fi);
            return true;
        }

        fileName = null;
        fi = default;
        return false;
    }
}

internal static class Program
{
    private static void LogCrash(string tag, object ex)
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(p, $"{DateTime.Now:O} [{tag}] {ex}\n");
        }
        catch { }
    }

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("unhandled", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("unobserved", e.Exception); e.SetObserved(); };

        string pass = null, mount = null;
        var serviceMode = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p": pass = args[++i]; break;
                case "-m": mount = args[++i]; break;
                case "--service": serviceMode = true; break;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    return 2;
            }
        }

        if (mount == null)
        {
            Console.Error.WriteLine("usage: SpdPool.Fs [-p <backing-dir>] -m <X:>");
            return 2;
        }

        if (pass == null)
        {
            var reg = MemberRegistry.ProbeDefault();
            foreach (var m in reg.Members)
                Console.WriteLine($"member {m.Id}: online={m.Online} total={m.TotalBytes / 1073741824.0:F1}GB free={m.FreeBytes / 1073741824.0:F1}GB {(m.Error != null ? "[" + m.Error + "]" : "")}");
            var online = reg.OnlineMembers;
            if (online.Count == 0)
            {
                Console.Error.WriteLine("no online pool member found");
                return 1;
            }
            var members = online.Select(m => new MemberFs
            {
                Id = m.Id,
                Root = m.BackendPath.EndsWith("\\") ? m.BackendPath : m.BackendPath + "\\",
                MountPath = m.MountPath,
                IsSsd = m.IsSsd,
            }).ToList();

            var debugLog2 = Path.Combine(Path.GetTempPath(), "spdpool-fs-debug.log");
            FileSystemHost.SetDebugLogFile(debugLog2);
            var host2 = new FileSystemHost(new UnionFileSystem(members));
            if (0 > host2.Mount(mount, null, true, 0xFFFFFFFF))
            {
                Console.Error.WriteLine("mount failed");
                return 1;
            }
            var migrator = new Migrator(members);
            migrator.Start(); // SSD tiering is on by default so the UI can show tasks
            var statsWriter = new StatsWriter();
            statsWriter.Start();
            Console.WriteLine($"mounted {host2.MountPoint()} -> union of {string.Join(", ", members.Select(m => m.Id))}");

            if (serviceMode)
            {
                var stop = new ManualResetEventSlim();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { migrator.Stop(); host2.Unmount(); } catch { }
                };
                Console.WriteLine("service mode: waiting for stop signal");
                var hb = Path.Combine(AppContext.BaseDirectory, "heartbeat.log");
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                while (!stop.IsSet)
                {
                    // Loop instead of a bare Wait(): if the event returns unexpectedly we must
                    // not fall through and silently unmount/exit the service under load.
                    if (stop.Wait(TimeSpan.FromSeconds(30))) break;
                    try
                    {
                        proc.Refresh();
                        File.AppendAllText(hb,
                            $"{DateTime.Now:O} pid={proc.Id} ws={proc.WorkingSet64 / 1048576}MB handles={proc.HandleCount} threads={proc.Threads.Count}\n");
                    }
                    catch { }
                }
            }
            else
            {
                Console.WriteLine("press Enter to unmount...");
                Console.ReadLine();
            }

            statsWriter.Stop();
            migrator.Stop();
            host2.Unmount();
            return 0;
        }

        var debugLog = Path.Combine(Path.GetTempPath(), "spdpool-fs-debug.log");
        FileSystemHost.SetDebugLogFile(debugLog);
        Console.WriteLine($"debug log: {debugLog}");

        var host = new FileSystemHost(new PassthroughFs(pass));
        if (0 > host.Mount(mount, null, true, 0xFFFFFFFF))
        {
            Console.Error.WriteLine("mount failed");
            return 1;
        }
        Console.WriteLine($"mounted {host.MountPoint()} -> {pass}");
        Console.WriteLine("press Enter to unmount...");
        Console.ReadLine();
        host.Unmount();
        return 0;
    }
}

internal sealed class UnreachableException : Exception
{
    public UnreachableException() : base("unreachable") { }
}
