// ClutterCutter — a single-file WinForms disk usage browser.
// Compile: csc /target:winexe /out:ClutterCutter.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.VisualBasic.dll ClutterCutter.cs

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;

namespace ClutterCutter
{
    public class FolderNode
    {
        public string FullPath;
        public string Name;
        public long Size;
        public long OwnSize;
        public long FileCount;
        public long FolderCount;
        public long DirectFileCount;
        public List<FolderNode> Children;
        public FolderNode Parent;
        public bool IsAccessDenied;
        public DateTime LastModified;

        public FolderNode() { Children = new List<FolderNode>(); }
    }

    public class ScanProgress
    {
        public long TotalSize;
        public long FilesScanned;
        public string CurrentPath;
        // -1 = unknown (indeterminate progress); 0..100 otherwise.
        public double Percent;
    }

    public class Scanner
    {
        // Win32 P/Invoke — using FindFirstFile/FindNextFile directly bypasses the FileInfo/DirectoryInfo
        // allocation cost which dominates the runtime when scanning millions of small files.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(
            string lpFileName,
            int fInfoLevelId,
            out WIN32_FIND_DATAW lpFindFileData,
            int fSearchOp,
            IntPtr lpSearchFilter,
            uint dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr hFindFile);

        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        const int ERROR_ACCESS_DENIED = 5;
        const int ERROR_FILE_NOT_FOUND = 2;
        const int ERROR_PATH_NOT_FOUND = 3;
        const int ERROR_NO_MORE_FILES = 18;
        // FindExInfoBasic skips the cAlternateFileName marshal (8.3 short names) — saves a copy per entry.
        const int FindExInfoBasic = 1;
        const int FindExSearchNameMatch = 0;
        // FIND_FIRST_EX_LARGE_FETCH tells Windows to use a larger I/O buffer; significant on modern OSes.
        const uint FIND_FIRST_EX_LARGE_FETCH = 0x2;

        struct SubDirRef { public string Path; public long LastWriteFt; }

        public IProgress<ScanProgress> Progress;
        public CancellationToken Token;
        public int ParallelTopLevels = 2;     // recursion levels at which to parallelize subdir scans
        // Approx total bytes to scan, used to compute % progress. 0 = unknown (indeterminate).
        public long TotalSizeHint;

        long _totalSize;
        long _filesScanned;
        long _lastReportTicks;

        public Scanner() { }

        public FolderNode Scan(string root)
        {
            string path = root == null ? "" : root.TrimEnd();
            // Don't strip trailing slash for drive roots like "C:\".
            if (path.Length > 3 && path.EndsWith("\\")) path = path.TrimEnd('\\');
            long rootFt = 0;
            try { rootFt = Directory.GetLastWriteTimeUtc(path).ToFileTimeUtc(); } catch { }
            return ScanFolder(path, null, true, ParallelTopLevels, rootFt);
        }

        static string ToLongPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        FolderNode ScanFolder(string path, FolderNode parent, bool isRoot, int parallelDepth, long lastWriteFt)
        {
            if (Token.IsCancellationRequested) Token.ThrowIfCancellationRequested();

            FolderNode node = new FolderNode();
            node.FullPath = path;
            node.Name = isRoot ? path : Path.GetFileName(path.TrimEnd('\\'));
            node.Parent = parent;
            if (lastWriteFt != 0)
            {
                try { node.LastModified = DateTime.FromFileTime(lastWriteFt); } catch { }
            }

            string findPath = path.EndsWith("\\") ? path + "*" : path + "\\*";
            if (findPath.Length > 240) findPath = ToLongPath(findPath);

            WIN32_FIND_DATAW fd;
            IntPtr h = FindFirstFileExW(findPath, FindExInfoBasic, out fd, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (h == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED) node.IsAccessDenied = true;
                return node;
            }

            List<SubDirRef> subdirs = null;
            try
            {
                do
                {
                    string name = fd.cFileName;
                    if (name == "." || name == "..") continue;

                    bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    bool isReparse = (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                    if (isDir)
                    {
                        if (isReparse) continue; // skip junctions/symlinks to avoid loops + double counting
                        if (subdirs == null) subdirs = new List<SubDirRef>(8);
                        SubDirRef sr;
                        sr.Path = path.EndsWith("\\") ? (path + name) : (path + "\\" + name);
                        sr.LastWriteFt = ((long)fd.ftLastWriteTime.dwHighDateTime << 32) | (uint)fd.ftLastWriteTime.dwLowDateTime;
                        subdirs.Add(sr);
                    }
                    else
                    {
                        long size = ((long)fd.nFileSizeHigh << 32) | (uint)fd.nFileSizeLow;
                        node.OwnSize += size;
                        node.Size += size;
                        node.DirectFileCount++;
                        node.FileCount++;
                        Interlocked.Increment(ref _filesScanned);
                        Interlocked.Add(ref _totalSize, size);
                    }
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }

            if (Token.IsCancellationRequested) Token.ThrowIfCancellationRequested();

            if (subdirs != null && subdirs.Count > 0)
            {
                if (parallelDepth > 0 && subdirs.Count > 1)
                {
                    FolderNode[] children = new FolderNode[subdirs.Count];
                    ParallelOptions po = new ParallelOptions();
                    po.MaxDegreeOfParallelism = Environment.ProcessorCount;
                    po.CancellationToken = Token;
                    int childParallel = parallelDepth - 1;
                    List<SubDirRef> dirs = subdirs;
                    try
                    {
                        Parallel.For(0, dirs.Count, po, delegate(int i)
                        {
                            SubDirRef sr = dirs[i];
                            children[i] = ScanFolder(sr.Path, node, false, childParallel, sr.LastWriteFt);
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    for (int i = 0; i < children.Length; i++)
                    {
                        FolderNode c = children[i];
                        if (c == null) continue;
                        node.Children.Add(c);
                        node.Size += c.Size;
                        node.FileCount += c.FileCount;
                        node.FolderCount += c.FolderCount + 1;
                    }
                }
                else
                {
                    for (int i = 0; i < subdirs.Count; i++)
                    {
                        if (Token.IsCancellationRequested) Token.ThrowIfCancellationRequested();
                        SubDirRef sr = subdirs[i];
                        FolderNode c = ScanFolder(sr.Path, node, false, 0, sr.LastWriteFt);
                        node.Children.Add(c);
                        node.Size += c.Size;
                        node.FileCount += c.FileCount;
                        node.FolderCount += c.FolderCount + 1;
                    }
                }
            }

            ReportProgress(path);
            return node;
        }

        void ReportProgress(string path)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastReportTicks);
            // Throttle to ~12 reports/sec across all threads
            if (nowTicks - last < TimeSpan.TicksPerMillisecond * 80) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, nowTicks, last) != last) return;
            if (Progress == null) return;
            ScanProgress p = new ScanProgress();
            p.TotalSize = Interlocked.Read(ref _totalSize);
            p.FilesScanned = Interlocked.Read(ref _filesScanned);
            p.CurrentPath = path;
            if (TotalSizeHint > 0)
            {
                double pct = 100.0 * p.TotalSize / TotalSizeHint;
                if (pct < 0) pct = 0; if (pct > 99.5) pct = 99.5; // never show "100%" until truly done
                p.Percent = pct;
            }
            else { p.Percent = -1; }
            Progress.Report(p);
        }
    }

    // -------------------------------------------------------------------------
    // MftScanner: reads the NTFS Master File Table directly via raw volume reads.
    // Requires admin and an NTFS volume. ~5-10x faster than the FindFirstFileEx
    // walker because it reads file metadata in one big sequential pass instead of
    // doing per-folder syscalls. We treat the requested path as a drive root and
    // scan the entire volume; subfolders are reachable via the returned tree.
    // -------------------------------------------------------------------------
    public class MftScanner
    {
        // ---- P/Invoke ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove,
            out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetVolumeInformationW(string lpRootPathName, StringBuilder lpVolumeNameBuffer,
            int nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint FILE_SHARE_DELETE = 0x4;
        const uint OPEN_EXISTING = 3;
        const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
        const uint FILE_BEGIN = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct NTFS_VOLUME_DATA_BUFFER
        {
            public long VolumeSerialNumber;
            public long NumberSectors;
            public long TotalClusters;
            public long FreeClusters;
            public long TotalReserved;
            public uint BytesPerSector;
            public uint BytesPerCluster;
            public uint BytesPerFileRecordSegment;
            public uint ClustersPerFileRecordSegment;
            public long MftValidDataLength;
            public long MftStartLcn;
            public long Mft2StartLcn;
            public long MftZoneStart;
            public long MftZoneEnd;
        }

        // ---- Public API ----
        public IProgress<ScanProgress> Progress;
        public CancellationToken Token;

        long _filesScanned;
        long _totalSize;
        long _lastReportTicks;
        long _mftBytesTotal;
        long _mftBytesRead;

        // Internal entry built from a parsed MFT record.
        class MftEntry
        {
            public long Frn;
            public long ParentFrn;
            public string Name;
            public long Size;
            public bool IsDir;
            public bool IsInUse;
            public DateTime LastWrite;
            public List<MftEntry> Kids; // populated during tree-build
        }

        struct DataRun
        {
            public long Lcn;     // physical starting cluster (absolute)
            public long Length;  // length in clusters
        }

        public static bool IsNtfsDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string trimmed = path.Trim();
            if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += "\\";
            if (trimmed.Length != 3 || trimmed[1] != ':' || trimmed[2] != '\\') return false;
            try
            {
                var sbFs = new StringBuilder(20);
                var sbLabel = new StringBuilder(64);
                uint serial, maxLen, flags;
                if (!GetVolumeInformationW(trimmed, sbLabel, sbLabel.Capacity, out serial, out maxLen, out flags, sbFs, sbFs.Capacity))
                    return false;
                return sbFs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public FolderNode Scan(string root)
        {
            if (root == null) throw new ArgumentNullException("root");
            string norm = root.TrimEnd('\\').Trim();
            if (norm.Length < 2 || norm[1] != ':') throw new ArgumentException("MFT scan requires a drive-letter root (e.g. C:)");
            char drive = char.ToUpperInvariant(norm[0]);
            string volPath = "\\\\.\\" + drive + ":";

            // CreateFile against \\.\C: needs admin. Caller checks IsNtfsDriveRoot first.
            using (SafeFileHandle h = CreateFileW(volPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + volPath + " (run as Administrator).");

                NTFS_VOLUME_DATA_BUFFER vd = GetVolumeData(h);
                int recordSize = (int)vd.BytesPerFileRecordSegment;
                int sectorSize = (int)vd.BytesPerSector;
                long bytesPerCluster = vd.BytesPerCluster;
                Program.Log(string.Format("MFT: drive={0} recSize={1} sec={2} cluster={3} mftLen={4} mftLcn={5}",
                    drive, recordSize, sectorSize, bytesPerCluster, vd.MftValidDataLength, vd.MftStartLcn));

                // Read MFT record 0 (the MFT's own record), parse its $DATA data runs.
                byte[] rec0 = new byte[recordSize];
                ReadAt(h, vd.MftStartLcn * bytesPerCluster, rec0, 0, recordSize);
                ApplyFixups(rec0, 0, recordSize, sectorSize);
                List<DataRun> runs = ExtractMftDataRuns(rec0, 0);
                if (runs == null || runs.Count == 0)
                    throw new InvalidOperationException("Could not parse MFT data runs from record 0.");

                long estCount = vd.MftValidDataLength / recordSize;
                Program.Log("MFT: ~" + estCount + " records across " + runs.Count + " runs");

                Dictionary<long, MftEntry> entries = new Dictionary<long, MftEntry>(estCount > 0 ? (int)Math.Min(estCount, 4000000) : 100000);

                long frnCursor = 0;
                long totalBytesRemaining = vd.MftValidDataLength;
                _mftBytesTotal = vd.MftValidDataLength;
                _mftBytesRead = 0;
                const int chunkSize = 4 * 1024 * 1024; // 4 MB read buffer
                byte[] buf = new byte[chunkSize];
                GCHandle hPin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    IntPtr bufPtr = hPin.AddrOfPinnedObject();
                    for (int ri = 0; ri < runs.Count && totalBytesRemaining > 0; ri++)
                    {
                        if (Token.IsCancellationRequested) Token.ThrowIfCancellationRequested();
                        long pos = runs[ri].Lcn * bytesPerCluster;
                        long runBytes = runs[ri].Length * bytesPerCluster;
                        while (runBytes > 0 && totalBytesRemaining > 0)
                        {
                            if (Token.IsCancellationRequested) Token.ThrowIfCancellationRequested();
                            long want = Math.Min(chunkSize, Math.Min(runBytes, totalBytesRemaining));
                            int toRead = (int)(want - (want % recordSize));
                            if (toRead == 0) break;

                            SetPos(h, pos);
                            uint actuallyRead;
                            if (!ReadFile(h, bufPtr, (uint)toRead, out actuallyRead, IntPtr.Zero))
                                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ReadFile MFT failed");
                            if (actuallyRead == 0) break;
                            int processBytes = (int)actuallyRead;
                            processBytes -= processBytes % recordSize;

                            for (int off = 0; off < processBytes; off += recordSize)
                            {
                                ApplyFixups(buf, off, recordSize, sectorSize);
                                ProcessRecord(buf, off, recordSize, frnCursor, entries);
                                frnCursor++;
                            }
                            pos += actuallyRead;
                            runBytes -= actuallyRead;
                            totalBytesRemaining -= actuallyRead;
                            Interlocked.Add(ref _mftBytesRead, (long)actuallyRead);

                            // Throttled progress
                            ReportProgress();
                        }
                    }
                }
                finally { hPin.Free(); }

                Program.Log("MFT: parsed " + entries.Count + " entries, building tree");
                return BuildTree(entries, drive);
            }
        }

        // ---- Volume helpers ----
        static NTFS_VOLUME_DATA_BUFFER GetVolumeData(SafeFileHandle h)
        {
            int sz = Marshal.SizeOf(typeof(NTFS_VOLUME_DATA_BUFFER));
            IntPtr p = Marshal.AllocHGlobal(sz);
            try
            {
                uint ret;
                if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, p, (uint)sz, out ret, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_NTFS_VOLUME_DATA failed");
                return (NTFS_VOLUME_DATA_BUFFER)Marshal.PtrToStructure(p, typeof(NTFS_VOLUME_DATA_BUFFER));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        static void SetPos(SafeFileHandle h, long pos)
        {
            long np;
            if (!SetFilePointerEx(h, pos, out np, FILE_BEGIN))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetFilePointerEx failed");
        }

        static void ReadAt(SafeFileHandle h, long pos, byte[] buf, int offset, int len)
        {
            SetPos(h, pos);
            GCHandle gc = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                IntPtr p = (IntPtr)(gc.AddrOfPinnedObject().ToInt64() + offset);
                uint got;
                if (!ReadFile(h, p, (uint)len, out got, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed at " + pos);
            }
            finally { gc.Free(); }
        }

        // ---- Fixups: replace the last 2 bytes of each 512-byte sector with the saved values ----
        static void ApplyFixups(byte[] buf, int recOff, int recSize, int sectorSize)
        {
            if (buf.Length < recOff + 8) return;
            // "FILE" or "BAAD" magic in first 4 bytes
            if (buf[recOff] != 0x46 || buf[recOff + 1] != 0x49 || buf[recOff + 2] != 0x4C || buf[recOff + 3] != 0x45) return;
            ushort usaOffset = BitConverter.ToUInt16(buf, recOff + 4);
            ushort usaCount = BitConverter.ToUInt16(buf, recOff + 6);
            if (usaCount < 1) return;
            int usaPos = recOff + usaOffset;
            // USA[0] is the value that *should* be at the end of each sector
            // USA[1..count-1] is what to put back
            for (int i = 1; i < usaCount; i++)
            {
                int sectorEnd = recOff + i * sectorSize - 2;
                if (sectorEnd + 2 > recOff + recSize) break;
                buf[sectorEnd] = buf[usaPos + i * 2];
                buf[sectorEnd + 1] = buf[usaPos + i * 2 + 1];
            }
        }

        // ---- Parse MFT record 0's $DATA data runs (which describe where the MFT itself lives) ----
        static List<DataRun> ExtractMftDataRuns(byte[] rec, int recOff)
        {
            // Walk attributes, find non-resident $DATA (type 0x80, name length 0)
            ushort firstAttr = BitConverter.ToUInt16(rec, recOff + 20);
            int p = recOff + firstAttr;
            int recEnd = recOff + rec.Length;
            while (p + 8 < recEnd)
            {
                uint type = BitConverter.ToUInt32(rec, p);
                if (type == 0xFFFFFFFF) break;
                uint len = BitConverter.ToUInt32(rec, p + 4);
                if (len == 0 || p + len > recEnd) break;
                if (type == 0x80 && rec[p + 8] == 1 && rec[p + 9] == 0)
                {
                    // Non-resident $DATA, unnamed
                    ushort runOffset = BitConverter.ToUInt16(rec, p + 32);
                    int runStart = p + runOffset;
                    return ParseDataRuns(rec, runStart, p + (int)len);
                }
                p += (int)len;
            }
            return new List<DataRun>();
        }

        static List<DataRun> ParseDataRuns(byte[] buf, int start, int end)
        {
            List<DataRun> runs = new List<DataRun>();
            long prevLcn = 0;
            int p = start;
            while (p < end)
            {
                byte header = buf[p++];
                if (header == 0) break;
                int lenBytes = header & 0x0F;
                int offBytes = (header >> 4) & 0x0F;
                if (lenBytes == 0) break;
                long length = ReadSignedLE(buf, p, lenBytes);
                p += lenBytes;
                long offset = 0;
                if (offBytes > 0)
                {
                    offset = ReadSignedLE(buf, p, offBytes);
                    p += offBytes;
                }
                else
                {
                    // Sparse run — skip
                    continue;
                }
                long lcn = prevLcn + offset; // first run: prev=0, so offset is absolute
                prevLcn = lcn;
                DataRun r;
                r.Lcn = lcn;
                r.Length = length;
                runs.Add(r);
            }
            return runs;
        }

        static long ReadSignedLE(byte[] buf, int off, int len)
        {
            // Little-endian signed integer of 'len' bytes (1..8). Sign-extend the top byte.
            long v = 0;
            for (int i = 0; i < len; i++) v |= ((long)buf[off + i]) << (i * 8);
            // Sign extend
            if (len < 8 && (buf[off + len - 1] & 0x80) != 0)
                v |= ~((1L << (len * 8)) - 1);
            return v;
        }

        // ---- Parse one MFT record into an MftEntry ----
        void ProcessRecord(byte[] buf, int off, int recSize, long frn, Dictionary<long, MftEntry> entries)
        {
            if (off + recSize > buf.Length) return;
            // FILE magic check
            if (buf[off] != 0x46 || buf[off + 1] != 0x49 || buf[off + 2] != 0x4C || buf[off + 3] != 0x45) return;

            ushort flags = BitConverter.ToUInt16(buf, off + 22);
            bool inUse = (flags & 0x01) != 0;
            bool isDir = (flags & 0x02) != 0;
            if (!inUse) return;

            ushort firstAttrOff = BitConverter.ToUInt16(buf, off + 20);
            int p = off + firstAttrOff;
            int recEnd = off + recSize;

            string bestName = null;
            byte bestNs = 0xFF;       // 1=Win32, 3=Win32&DOS, 0=POSIX, 2=DOS
            long parentFrn = -1;
            long size = 0;
            bool sizeFound = false;
            long lastWriteFt = 0;

            while (p + 16 < recEnd)
            {
                uint type = BitConverter.ToUInt32(buf, p);
                if (type == 0xFFFFFFFF) break;
                uint alen = BitConverter.ToUInt32(buf, p + 4);
                if (alen == 0 || p + alen > recEnd) break;

                byte nonResident = buf[p + 8];
                byte attrNameLen = buf[p + 9];

                if (type == 0x10 && nonResident == 0)
                {
                    // $STANDARD_INFORMATION (resident): mod time at offset+8 within value
                    ushort vOff = BitConverter.ToUInt16(buf, p + 20);
                    int v = p + vOff;
                    if (v + 16 <= recEnd)
                    {
                        long modFt = BitConverter.ToInt64(buf, v + 8);
                        if (lastWriteFt == 0) lastWriteFt = modFt;
                    }
                }
                else if (type == 0x30 && nonResident == 0)
                {
                    // $FILE_NAME (resident)
                    ushort vOff = BitConverter.ToUInt16(buf, p + 20);
                    int v = p + vOff;
                    if (v + 66 <= recEnd)
                    {
                        long pFrnRaw = BitConverter.ToInt64(buf, v);
                        long pFrn = pFrnRaw & 0x0000FFFFFFFFFFFFL;
                        long modFt = BitConverter.ToInt64(buf, v + 16);
                        long realSizeFromName = BitConverter.ToInt64(buf, v + 48);
                        byte nameLen = buf[v + 64];
                        byte ns = buf[v + 65];
                        int nameByteLen = nameLen * 2;
                        if (v + 66 + nameByteLen <= recEnd)
                        {
                            string name = Encoding.Unicode.GetString(buf, v + 66, nameByteLen);
                            // Prefer Win32(1) or Win32&DOS(3); fall back to anything else.
                            int prio = NamePriority(ns);
                            int curPrio = bestName == null ? -1 : NamePriority(bestNs);
                            if (prio > curPrio)
                            {
                                bestName = name;
                                bestNs = ns;
                                parentFrn = pFrn;
                                if (lastWriteFt == 0) lastWriteFt = modFt;
                                if (isDir && !sizeFound) { size = 0; }
                            }
                        }
                    }
                }
                else if (type == 0x80 && attrNameLen == 0 && !sizeFound)
                {
                    // $DATA, default unnamed stream — only count once, prefer first
                    if (nonResident == 0)
                    {
                        uint valLen = BitConverter.ToUInt32(buf, p + 16);
                        size = valLen;
                    }
                    else
                    {
                        // Non-resident: real size at offset 48
                        if (p + 56 <= recEnd) size = BitConverter.ToInt64(buf, p + 48);
                    }
                    sizeFound = true;
                }

                p += (int)alen;
            }

            if (bestName == null) return;
            if (isDir) size = 0;

            MftEntry e = new MftEntry();
            e.Frn = frn;
            e.ParentFrn = parentFrn;
            e.Name = bestName;
            e.Size = size;
            e.IsDir = isDir;
            e.IsInUse = inUse;
            try { e.LastWrite = lastWriteFt > 0 ? DateTime.FromFileTime(lastWriteFt) : DateTime.MinValue; }
            catch { e.LastWrite = DateTime.MinValue; }

            entries[frn] = e;

            if (!isDir)
            {
                Interlocked.Increment(ref _filesScanned);
                Interlocked.Add(ref _totalSize, size);
            }
        }

        static int NamePriority(byte ns)
        {
            // Win32&DOS combined > Win32 > POSIX > DOS
            switch (ns) { case 3: return 4; case 1: return 3; case 0: return 2; case 2: return 1; default: return 0; }
        }

        // ---- Build a FolderNode tree, starting from root FRN=5 (NTFS root) ----
        FolderNode BuildTree(Dictionary<long, MftEntry> entries, char drive)
        {
            // Root FRN of NTFS volume is 5 ($Root). It exists as a directory entry.
            if (!entries.ContainsKey(5)) throw new InvalidOperationException("MFT root entry (FRN 5) not found.");

            // Wire up children lists. We need entries[parentFrn].Kids.Add(entry). Create empty lists lazily.
            foreach (KeyValuePair<long, MftEntry> kv in entries)
            {
                MftEntry e = kv.Value;
                if (e.Frn == 5) continue; // root has no parent
                MftEntry parent;
                if (!entries.TryGetValue(e.ParentFrn, out parent)) continue;
                if (parent.Kids == null) parent.Kids = new List<MftEntry>();
                parent.Kids.Add(e);
            }

            string rootPath = drive + ":\\";
            FolderNode rootNode = new FolderNode();
            rootNode.FullPath = rootPath;
            rootNode.Name = rootPath;
            rootNode.LastModified = entries[5].LastWrite;

            // Recursive build (iterative would be cleaner for very deep trees; NTFS depth limit is ~255)
            BuildSubtree(entries[5], rootNode, rootPath);
            return rootNode;
        }

        void BuildSubtree(MftEntry me, FolderNode node, string nodePath)
        {
            if (me.Kids == null) return;
            for (int i = 0; i < me.Kids.Count; i++)
            {
                MftEntry c = me.Kids[i];
                if (c.IsDir)
                {
                    FolderNode child = new FolderNode();
                    string childPath = nodePath.EndsWith("\\") ? nodePath + c.Name : nodePath + "\\" + c.Name;
                    child.FullPath = childPath;
                    child.Name = c.Name;
                    child.Parent = node;
                    child.LastModified = c.LastWrite;
                    BuildSubtree(c, child, childPath);
                    node.Children.Add(child);
                    node.Size += child.Size;
                    node.FileCount += child.FileCount;
                    node.FolderCount += child.FolderCount + 1;
                }
                else
                {
                    node.OwnSize += c.Size;
                    node.Size += c.Size;
                    node.DirectFileCount++;
                    node.FileCount++;
                }
            }
        }

        void ReportProgress()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastReportTicks);
            if (nowTicks - last < TimeSpan.TicksPerMillisecond * 80) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, nowTicks, last) != last) return;
            if (Progress == null) return;
            ScanProgress p = new ScanProgress();
            p.TotalSize = Interlocked.Read(ref _totalSize);
            // We reserve the last ~5% for tree-build, so cap reading-progress at 95%.
            if (_mftBytesTotal > 0)
            {
                double pct = 95.0 * Interlocked.Read(ref _mftBytesRead) / _mftBytesTotal;
                if (pct < 0) pct = 0; if (pct > 95) pct = 95;
                p.Percent = pct;
            }
            else { p.Percent = -1; }
            p.FilesScanned = Interlocked.Read(ref _filesScanned);
            p.CurrentPath = "MFT scan in progress...";
            Progress.Report(p);
        }
    }

    public static class Sizes
    {
        public static string Format(long bytes)
        {
            if (bytes < 1024) return bytes.ToString() + " B";
            double v = bytes / 1024.0;
            string[] units = new string[] { "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            string fmt = v >= 100 ? "{0:F0} {1}" : v >= 10 ? "{0:F1} {1}" : "{0:F2} {1}";
            return string.Format(fmt, v, units[i]);
        }

        public static string FormatCount(long n) { return n.ToString("N0"); }
    }

    // Clickable card showing a drive with its used/total bar.
    public class DriveCard : Button
    {
        public DriveInfo Drive;
        public string LabelLine1;
        public string LabelLine2;
        public long UsedBytes;
        public long TotalBytes;
        public bool IsSelected;

        public DriveCard()
        {
            Width = 300;
            Height = 60;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = Color.FromArgb(190, 190, 190);
            BackColor = Color.White;
            Margin = new Padding(0, 0, 0, 6);
            Font = new Font("Segoe UI", 9F);
            Cursor = Cursors.Hand;
            Text = "";
            UseCompatibleTextRendering = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rc = ClientRectangle;
            Color bg = IsSelected ? Theme.CardSelectedBg : Theme.CtrlBg;
            using (SolidBrush sb = new SolidBrush(bg)) g.FillRectangle(sb, rc);
            Color border = IsSelected ? Theme.CardSelectedBorder : Theme.Border;
            using (Pen p = new Pen(border, IsSelected ? 2 : 1))
                g.DrawRectangle(p, 0, 0, rc.Width - 1, rc.Height - 1);

            using (Font f = new Font("Segoe UI", 10.5F, FontStyle.Bold))
            using (SolidBrush textB = new SolidBrush(Theme.Text))
                g.DrawString(LabelLine1 == null ? "" : LabelLine1, f, textB, 8, 4);
            using (SolidBrush sb = new SolidBrush(Theme.SubText))
                g.DrawString(LabelLine2 == null ? "" : LabelLine2, Font, sb, 8, 24);

            int barY = rc.Height - 14;
            Rectangle bar = new Rectangle(8, barY, rc.Width - 16, 8);
            Color trackBg = Theme.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(230, 230, 230);
            using (SolidBrush sb = new SolidBrush(trackBg))
                g.FillRectangle(sb, bar);
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, bar);
            if (TotalBytes > 0)
            {
                double pct = (double)UsedBytes / TotalBytes;
                if (pct < 0) pct = 0; if (pct > 1) pct = 1;
                int w = (int)((bar.Width - 1) * pct);
                if (w > 0)
                {
                    Color c = pct > 0.9 ? Color.Crimson : pct > 0.75 ? Color.DarkOrange : Color.SteelBlue;
                    Rectangle fill = new Rectangle(bar.X + 1, bar.Y + 1, w, bar.Height - 1);
                    using (SolidBrush sb = new SolidBrush(c)) g.FillRectangle(sb, fill);
                }
            }
        }
    }

    public class BarListView : ListView
    {
        public int BarColumnIndex = 1;
        public BarListView()
        {
            OwnerDraw = true;
            DoubleBuffered = true;
            DrawColumnHeader += new DrawListViewColumnHeaderEventHandler(BarListView_DrawColumnHeader);
            DrawSubItem += new DrawListViewSubItemEventHandler(BarListView_DrawSubItem);
        }

        void BarListView_DrawColumnHeader(object s, DrawListViewColumnHeaderEventArgs e)
        {
            if (!Theme.Dark) { e.DrawDefault = true; return; }
            using (SolidBrush b = new SolidBrush(Theme.HeaderBg)) e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
            else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
            else flags |= TextFormatFlags.Left;
            Rectangle txtRect = e.Bounds; txtRect.Inflate(-6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, txtRect, Theme.Text, flags);
        }

        void BarListView_DrawSubItem(object s, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != BarColumnIndex)
            {
                if (!Theme.Dark) { e.DrawDefault = true; return; }
                bool selDark = (e.ItemState & ListViewItemStates.Selected) != 0;
                Color backDark = selDark ? Theme.SelectionBg : Theme.CtrlBg;
                Color fgDark = selDark ? Theme.SelectionText : (e.Item.ForeColor != Color.Empty && e.Item.ForeColor != SystemColors.WindowText ? e.Item.ForeColor : Theme.Text);
                using (SolidBrush b = new SolidBrush(backDark)) e.Graphics.FillRectangle(b, e.Bounds);
                TextFormatFlags tff = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                ColumnHeader col = ((ListView)s).Columns[e.ColumnIndex];
                if (col.TextAlign == HorizontalAlignment.Right) tff |= TextFormatFlags.Right;
                else if (col.TextAlign == HorizontalAlignment.Center) tff |= TextFormatFlags.HorizontalCenter;
                else tff |= TextFormatFlags.Left;
                Rectangle r2 = e.Bounds; r2.Inflate(-4, 0);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.Item.Font, r2, fgDark, tff);
                return;
            }

            double pct = 0;
            double.TryParse(e.SubItem.Tag == null ? "0" : e.SubItem.Tag.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out pct);
            if (pct < 0) pct = 0; if (pct > 1) pct = 1;

            Rectangle r = e.Bounds;
            bool selected = (e.ItemState & ListViewItemStates.Selected) != 0;
            Color back = selected ? Theme.SelectionBg : Theme.CtrlBg;
            using (SolidBrush sb = new SolidBrush(back)) e.Graphics.FillRectangle(sb, r);

            int pad = 3;
            Rectangle bar = new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2);
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, bar);
            int fillW = (int)((bar.Width - 1) * pct);
            if (fillW > 0)
            {
                Rectangle fill = new Rectangle(bar.X + 1, bar.Y + 1, fillW, bar.Height - 1);
                Color c1 = ColorForPct(pct);
                Color c2 = Color.FromArgb(180, c1);
                using (LinearGradientBrush lg = new LinearGradientBrush(fill, c1, c2, LinearGradientMode.Vertical))
                    e.Graphics.FillRectangle(lg, fill);
            }
            string text = (pct * 100).ToString("F1") + "%";
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                Color textColor = selected ? Theme.SelectionText : Theme.Text;
                using (SolidBrush sb = new SolidBrush(textColor))
                    e.Graphics.DrawString(text, e.Item.Font, sb, r, sf);
            }
        }

        static Color ColorForPct(double pct)
        {
            if (pct < 0.5)
            {
                int g = 200; int r = (int)(pct * 2 * 220);
                return Color.FromArgb(r, g, 60);
            }
            else
            {
                int r = 220; int g = (int)((1 - (pct - 0.5) * 2) * 200);
                return Color.FromArgb(r, g, 60);
            }
        }
    }

    // ---- Native helpers to dark-mode the title bar and the kernel-painted scroll bars. ----
    static class NativeTheme
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        // ---- Undocumented uxtheme ordinals (Win10 1809+) that make Win32 controls render in dark mode.
        // Without these, SetWindowTheme("DarkMode_Explorer") has no effect on scroll bars / list separators.
        // These are widely used by Notepad++, FAR, etc. They may break on future Windows builds; wrap in try/catch.
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int appMode);   // 0=default, 1=allow-dark, 2=force-dark, 3=force-light

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // Win10 2004+ official
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Win10 1809-1903 undocumented

        static bool _appModeSet;

        public static void EnsureAppMode(bool dark)
        {
            // Tell the OS our process opts in to dark controls. Safe to call multiple times.
            try { SetPreferredAppMode(dark ? 1 : 3); _appModeSet = true; } catch { }
            try { FlushMenuThemes(); } catch { }
        }

        public static void ApplyTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); } catch { }
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int)); } catch { }
        }

        public static void ApplyControlTheme(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            if (dark && !_appModeSet) EnsureAppMode(true);
            try { AllowDarkModeForWindow(hwnd, dark); } catch { }
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }
    }

    // Custom progress bar that respects Theme.Dark — the native ProgressBar control is fully
    // OS-themed and ignores BackColor.
    public class ThemedProgressBar : Control
    {
        int _value;
        int _maximum = 100;
        bool _marquee;
        int _marqueePos;
        System.Windows.Forms.Timer _marqueeTimer;

        public ThemedProgressBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 18;
            Theme.Changed += new EventHandler(delegate(object s, EventArgs e) { Invalidate(); });
        }

        public int Value
        {
            get { return _value; }
            set { int v = value; if (v < 0) v = 0; if (v > _maximum) v = _maximum; if (v != _value) { _value = v; Invalidate(); } }
        }
        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(1, value); Invalidate(); }
        }
        public int Minimum { get { return 0; } set { /* always 0 */ } }
        public int MarqueeAnimationSpeed
        {
            get { return _marqueeTimer != null ? _marqueeTimer.Interval : 30; }
            set { if (_marqueeTimer != null) _marqueeTimer.Interval = Math.Max(10, value); }
        }
        public ProgressBarStyle Style
        {
            get { return _marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; }
            set
            {
                bool m = (value == ProgressBarStyle.Marquee);
                if (m == _marquee) return;
                _marquee = m;
                if (_marquee)
                {
                    if (_marqueeTimer == null)
                    {
                        _marqueeTimer = new System.Windows.Forms.Timer();
                        _marqueeTimer.Interval = 30;
                        _marqueeTimer.Tick += new EventHandler(delegate(object s, EventArgs e)
                        {
                            _marqueePos += 6;
                            if (_marqueePos > Width + 60) _marqueePos = -60;
                            Invalidate();
                        });
                    }
                    _marqueePos = -60;
                    _marqueeTimer.Start();
                }
                else if (_marqueeTimer != null) _marqueeTimer.Stop();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            Color trackBg = Theme.Dark ? Color.FromArgb(50, 50, 53) : Color.FromArgb(225, 225, 225);
            using (SolidBrush b = new SolidBrush(trackBg)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);

            if (_marquee)
            {
                int barW = 60;
                int x = _marqueePos;
                int drawX = Math.Max(1, x);
                int drawW = Math.Min(barW, r.Width - drawX - 1);
                if (x + barW > 1 && x < r.Width - 1 && drawW > 0)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, drawX, 1, drawW, r.Height - 2);
                }
            }
            else if (_value > 0)
            {
                int w = (int)((double)_value / _maximum * (r.Width - 2));
                if (w > 0)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, 1, 1, w, r.Height - 2);
                }
            }
        }
    }

    public enum ThemeMode { Auto, Light, Dark }

    // ---- Theme: light/dark color set used by every custom-painted control. ----
    public static class Theme
    {
        public static ThemeMode Mode = ThemeMode.Auto;
        public static bool Dark { get; private set; }

        // Read the Windows "Apps use light theme" registry key.
        public static bool IsOsDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object o = k.GetValue("AppsUseLightTheme");
                        if (o is int) return ((int)o) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void Refresh()
        {
            switch (Mode)
            {
                case ThemeMode.Light: Dark = false; break;
                case ThemeMode.Dark:  Dark = true;  break;
                default:              Dark = IsOsDark(); break;
            }
        }

        public static Color FormBg          { get { return Dark ? Color.FromArgb(30, 30, 30)    : SystemColors.Control; } }
        public static Color HeaderBg        { get { return Dark ? Color.FromArgb(45, 45, 48)    : Color.FromArgb(245, 245, 245); } }
        public static Color PanelBg         { get { return Dark ? Color.FromArgb(37, 37, 38)    : Color.FromArgb(250, 250, 250); } }
        public static Color CtrlBg          { get { return Dark ? Color.FromArgb(37, 37, 38)    : Color.White; } }
        public static Color CtrlAltBg       { get { return Dark ? Color.FromArgb(50, 50, 53)    : Color.White; } }
        public static Color Text            { get { return Dark ? Color.FromArgb(220, 220, 220) : Color.Black; } }
        public static Color SubText         { get { return Dark ? Color.FromArgb(160, 160, 160) : Color.DimGray; } }
        public static Color Border          { get { return Dark ? Color.FromArgb(63, 63, 70)    : Color.FromArgb(190, 190, 190); } }
        public static Color SelectionBg     { get { return Dark ? Color.FromArgb(38, 79, 120)   : SystemColors.Highlight; } }
        public static Color SelectionText   { get { return Dark ? Color.White                   : SystemColors.HighlightText; } }
        public static Color Accent          { get { return Dark ? Color.FromArgb(86, 156, 214)  : Color.FromArgb(40, 100, 200); } }
        public static Color CardSelectedBg  { get { return Dark ? Color.FromArgb(56, 80, 110)   : Color.FromArgb(220, 235, 252); } }
        public static Color CardSelectedBorder { get { return Dark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(70, 130, 200); } }

        public static event EventHandler Changed;
        public static void RaiseChanged() { var h = Changed; if (h != null) h(null, EventArgs.Empty); }
    }

    // Custom ToolStrip/Menu/Status renderer that respects Theme.Dark.
    class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new ThemedColorTable()) { }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.HeaderBg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, e.AffectedBounds.Left, e.AffectedBounds.Bottom - 1, e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripItem it = e.Item;
            if (it != null && it.Selected) e.TextColor = Theme.SelectionText;
            else if (it is ToolStripLabel && ((ToolStripLabel)it).IsLink) e.TextColor = Theme.Accent;
            else e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle r = e.Item.Bounds;
            using (Pen p = new Pen(Theme.Border))
            {
                if (e.Vertical) e.Graphics.DrawLine(p, r.Width / 2, 4, r.Width / 2, r.Height - 4);
                else e.Graphics.DrawLine(p, 4, r.Height / 2, r.Width - 4, r.Height / 2);
            }
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                using (SolidBrush b = new SolidBrush(Theme.SelectionBg)) e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
            }
        }
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                using (SolidBrush b = new SolidBrush(Theme.SelectionBg)) e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
            }
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }
    }

    class ThemedColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.SelectionBg; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.SelectionBg; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.SelectionBg; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.SelectionBg; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.SelectionBg; } }
        public override Color MenuItemPressedGradientMiddle { get { return Theme.SelectionBg; } }
        public override Color ToolStripDropDownBackground { get { return Theme.CtrlBg; } }
        public override Color ImageMarginGradientBegin { get { return Theme.HeaderBg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.HeaderBg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.HeaderBg; } }
        public override Color ToolStripGradientBegin { get { return Theme.HeaderBg; } }
        public override Color ToolStripGradientMiddle { get { return Theme.HeaderBg; } }
        public override Color ToolStripGradientEnd { get { return Theme.HeaderBg; } }
        public override Color ToolStripBorder { get { return Theme.Border; } }
        public override Color ButtonSelectedHighlight { get { return Theme.SelectionBg; } }
        public override Color ButtonSelectedGradientBegin { get { return Theme.SelectionBg; } }
        public override Color ButtonSelectedGradientMiddle { get { return Theme.SelectionBg; } }
        public override Color ButtonSelectedGradientEnd { get { return Theme.SelectionBg; } }
        public override Color ButtonPressedGradientBegin { get { return Theme.SelectionBg; } }
        public override Color ButtonPressedGradientMiddle { get { return Theme.SelectionBg; } }
        public override Color ButtonPressedGradientEnd { get { return Theme.SelectionBg; } }
        public override Color StatusStripGradientBegin { get { return Theme.HeaderBg; } }
        public override Color StatusStripGradientEnd { get { return Theme.HeaderBg; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    public class MainForm : Form
    {
        ToolStrip toolbar;
        ToolStripButton scanBtn, stopBtn, refreshBtn, parentBtn, openBtn, topBtn;
        ToolStripTextBox customPathBox;
        ToolStripLabel customPathLabel;
        SplitContainer split;
        FlowLayoutPanel drivePanel;
        Label drivesHeader, foldersHeader;
        Panel leftPanel;
        TreeView tree;
        ToolStrip breadcrumbBar;
        BarListView list;
        ColumnHeader colName, colPct, colSize, colAlloc, colFiles, colFolders, colModified;
        StatusStrip status;
        ToolStripStatusLabel statusLabel, sizeLabel, countLabel, timeLabel, pctLabel;
        ThemedProgressBar progress;
        ToolStripControlHost progressHost;
        MenuStrip mainMenu;
        ToolStripMenuItem themeAutoItem, themeLightItem, themeDarkItem;
        ToolStripLabel brandLabelRef;
        ContextMenuStrip itemMenu;

        FolderNode rootNode;
        FolderNode selectedNode;
        CancellationTokenSource cts;
        DateTime scanStart;
        System.Windows.Forms.Timer elapsedTimer;
        bool scanning;
        string lastScannedPath;
        string scanModeUsed;
        bool topMode;
        DriveCard selectedCard;

        int sortColumn = 2;
        bool sortDesc = true;

        public MainForm()
        {
            Program.Log("MainForm ctor: begin");
            Text = Program.IsAdmin ? "ClutterCutter — Administrator" : "ClutterCutter — limited (no admin)";
            // Fit within the working area of the primary screen so we never start offscreen.
            Rectangle wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1024, 768);
            Width = Math.Min(1320, Math.Max(800, wa.Width - 80));
            Height = Math.Min(860, Math.Max(540, wa.Height - 80));
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            // Use the icon embedded in our own exe via /win32icon at compile time.
            try { Icon = Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location); }
            catch { Icon = SystemIcons.Application; }
            Theme.Mode = LoadThemeMode();
            Theme.Refresh();
            Program.Log("MainForm ctor: BuildUi");
            BuildUi();
            Program.Log("MainForm ctor: LoadDrives");
            LoadDrives();
            ApplyTheme(); // apply colors before first paint
            Program.Log("MainForm ctor: end (mode=" + Theme.Mode + " resolved=" + (Theme.Dark ? "dark" : "light") + ")");
            KeyPreview = true;
            this.KeyDown += new KeyEventHandler(MainForm_KeyDown);
            this.Shown += new EventHandler(MainForm_Shown);
            // Live OS-theme follow: when Mode==Auto and the user changes Windows theme, re-apply.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged +=
                new Microsoft.Win32.UserPreferenceChangedEventHandler(SystemEvents_UserPreferenceChanged);
            this.FormClosed += new FormClosedEventHandler(delegate(object s, FormClosedEventArgs ev) {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            });
        }

        void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (Theme.Mode != ThemeMode.Auto) return;
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            try
            {
                this.BeginInvoke(new Action(delegate
                {
                    bool wasDark = Theme.Dark;
                    Theme.Refresh();
                    if (Theme.Dark != wasDark) ApplyTheme();
                }));
            }
            catch { }
        }

        void MainForm_Shown(object sender, EventArgs e)
        {
            try
            {
                int width = split.Width > 0 ? split.Width : this.ClientSize.Width;
                int desired = Math.Max(280, Math.Min(380, width / 3));
                // Apply min sizes first (they're now safe because the splitter has real width).
                int p1Min = Math.Min(200, Math.Max(50, width / 5));
                int p2Min = Math.Min(400, Math.Max(50, width / 3));
                if (desired < p1Min) desired = p1Min;
                if (desired > width - p2Min - split.SplitterWidth - 4) desired = width - p2Min - split.SplitterWidth - 4;
                if (desired < 50) desired = 50;
                split.Panel1MinSize = p1Min;
                split.Panel2MinSize = p2Min;
                split.SplitterDistance = desired;
                Program.Log(string.Format("Shown: width={0} dist={1} p1Min={2} p2Min={3}", width, desired, p1Min, p2Min));

                // Re-apply native theme bits — handles only exist after the form is shown.
                NativeTheme.ApplyTitleBar(this.Handle, Theme.Dark);
                if (tree != null && tree.IsHandleCreated) NativeTheme.ApplyControlTheme(tree.Handle, Theme.Dark);
                if (list != null && list.IsHandleCreated) NativeTheme.ApplyControlTheme(list.Handle, Theme.Dark);
            }
            catch (Exception ex) { Program.Log("Shown handler: " + ex.ToString()); }
        }

        void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { DoRefresh(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && scanning) { DoStop(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && tree.Focused) { GoToParent(); e.Handled = true; }
        }

        void BuildUi()
        {
            // ---- Toolbar ----
            toolbar = new ToolStrip();
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.RenderMode = ToolStripRenderMode.System;
            toolbar.Padding = new Padding(4, 2, 4, 2);
            toolbar.ImageScalingSize = new Size(16, 16);

            scanBtn = new ToolStripButton("Scan");
            scanBtn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            scanBtn.Font = new Font(toolbar.Font, FontStyle.Bold);
            scanBtn.Click += new EventHandler(scanBtn_Click);

            stopBtn = new ToolStripButton("Stop");
            stopBtn.Enabled = false;
            stopBtn.Click += new EventHandler(stopBtn_Click);

            refreshBtn = new ToolStripButton("Refresh (F5)");
            refreshBtn.Click += new EventHandler(refreshBtn_Click);

            parentBtn = new ToolStripButton("Up");
            parentBtn.Click += new EventHandler(parentBtn_Click);

            openBtn = new ToolStripButton("Open in Explorer");
            openBtn.Click += new EventHandler(openBtn_Click);

            topBtn = new ToolStripButton("Top Largest Folders");
            topBtn.CheckOnClick = true;
            topBtn.ToolTipText = "Toggle: show the biggest folders/files anywhere inside the current selection (sorted by size).";
            topBtn.CheckedChanged += new EventHandler(topBtn_CheckedChanged);

            customPathLabel = new ToolStripLabel("Custom path:");
            customPathBox = new ToolStripTextBox();
            customPathBox.AutoSize = false;
            customPathBox.Width = 240;
            customPathBox.ToolTipText = "Type a custom folder path and press Enter to scan it.";
            customPathBox.KeyDown += new KeyEventHandler(customPathBox_KeyDown);
            ToolStripButton browseBtn = new ToolStripButton("Browse...");
            browseBtn.Click += new EventHandler(browseBtn_Click);

            // Help lives in the main menu bar (added below) — putting it on the crowded toolbar
            // pushed it into an overflow menu where users couldn't find it.

            toolbar.Items.Add(scanBtn);
            toolbar.Items.Add(stopBtn);
            toolbar.Items.Add(refreshBtn);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(parentBtn);
            toolbar.Items.Add(openBtn);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(topBtn);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(customPathLabel);
            toolbar.Items.Add(customPathBox);
            toolbar.Items.Add(browseBtn);

            // ---- Split ----
            // Defer SplitterDistance and MinSizes to MainForm_Shown — at construction time the container has
            // no real width and validation throws.
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;

            // ---- Left panel: Drives header + DrivePanel + Folders header + Tree ----
            leftPanel = new Panel();
            leftPanel.Dock = DockStyle.Fill;

            drivesHeader = new Label();
            drivesHeader.Text = "Drives — click to scan";
            drivesHeader.Dock = DockStyle.Top;
            drivesHeader.Height = 22;
            drivesHeader.Padding = new Padding(6, 4, 6, 0);
            drivesHeader.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            drivesHeader.BackColor = Color.FromArgb(245, 245, 245);

            drivePanel = new FlowLayoutPanel();
            drivePanel.Dock = DockStyle.Top;
            drivePanel.Height = 230;
            // AutoScroll only vertically — never let a stray horizontal scroll bar appear, since
            // we resize the cards to fit the panel's client width on every layout pass.
            drivePanel.AutoScroll = true;
            drivePanel.HorizontalScroll.Enabled = false;
            drivePanel.HorizontalScroll.Visible = false;
            drivePanel.HorizontalScroll.Maximum = 0;
            drivePanel.AutoScrollMinSize = new Size(0, 0);
            drivePanel.FlowDirection = FlowDirection.TopDown;
            drivePanel.WrapContents = false;
            drivePanel.Padding = new Padding(6);
            drivePanel.BackColor = Color.FromArgb(250, 250, 250);
            drivePanel.SizeChanged += new EventHandler(drivePanel_SizeChanged);
            drivePanel.Layout += new LayoutEventHandler(drivePanel_Layout);

            foldersHeader = new Label();
            foldersHeader.Text = "Folders (sorted by size)";
            foldersHeader.Dock = DockStyle.Top;
            foldersHeader.Height = 22;
            foldersHeader.Padding = new Padding(6, 4, 6, 0);
            foldersHeader.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            foldersHeader.BackColor = Color.FromArgb(245, 245, 245);

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.ShowLines = true;
            tree.ShowPlusMinus = true;
            tree.ShowRootLines = true;
            tree.FullRowSelect = true;
            tree.Font = new Font("Segoe UI", 9F);
            tree.AfterSelect += new TreeViewEventHandler(tree_AfterSelect);
            tree.BeforeExpand += new TreeViewCancelEventHandler(tree_BeforeExpand);
            tree.NodeMouseDoubleClick += new TreeNodeMouseClickEventHandler(tree_NodeDoubleClick);

            leftPanel.Controls.Add(tree);
            leftPanel.Controls.Add(foldersHeader);
            leftPanel.Controls.Add(drivePanel);
            leftPanel.Controls.Add(drivesHeader);
            split.Panel1.Controls.Add(leftPanel);

            // ---- Right panel: breadcrumb + list ----
            breadcrumbBar = new ToolStrip();
            breadcrumbBar.GripStyle = ToolStripGripStyle.Hidden;
            breadcrumbBar.RenderMode = ToolStripRenderMode.System;
            breadcrumbBar.AutoSize = false;
            breadcrumbBar.Height = 28;
            breadcrumbBar.Dock = DockStyle.Top;
            breadcrumbBar.Padding = new Padding(4, 2, 4, 2);
            breadcrumbBar.BackColor = Color.FromArgb(245, 245, 245);

            list = new BarListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.MultiSelect = true;
            list.Font = new Font("Segoe UI", 9F);

            colName = new ColumnHeader(); colName.Text = "Name"; colName.Width = 360;
            colPct = new ColumnHeader(); colPct.Text = "% of Parent"; colPct.Width = 220;
            colSize = new ColumnHeader(); colSize.Text = "Size"; colSize.Width = 110; colSize.TextAlign = HorizontalAlignment.Right;
            colAlloc = new ColumnHeader(); colAlloc.Text = "Own Files"; colAlloc.Width = 100; colAlloc.TextAlign = HorizontalAlignment.Right;
            colFiles = new ColumnHeader(); colFiles.Text = "Files"; colFiles.Width = 90; colFiles.TextAlign = HorizontalAlignment.Right;
            colFolders = new ColumnHeader(); colFolders.Text = "Folders"; colFolders.Width = 80; colFolders.TextAlign = HorizontalAlignment.Right;
            colModified = new ColumnHeader(); colModified.Text = "Modified"; colModified.Width = 140;
            list.Columns.AddRange(new ColumnHeader[] { colName, colPct, colSize, colAlloc, colFiles, colFolders, colModified });
            list.BarColumnIndex = 1;
            list.DoubleClick += new EventHandler(list_DoubleClick);
            list.ColumnClick += new ColumnClickEventHandler(list_ColumnClick);
            list.KeyDown += new KeyEventHandler(list_KeyDown);

            itemMenu = new ContextMenuStrip();
            ToolStripMenuItem miOpen = new ToolStripMenuItem("Open in Explorer"); miOpen.Click += new EventHandler(miOpen_Click);
            ToolStripMenuItem miDrill = new ToolStripMenuItem("Drill into folder"); miDrill.Click += new EventHandler(miDrill_Click);
            ToolStripMenuItem miCopy = new ToolStripMenuItem("Copy path"); miCopy.Click += new EventHandler(miCopy_Click);
            ToolStripMenuItem miCmd = new ToolStripMenuItem("Open Command Prompt here"); miCmd.Click += new EventHandler(miCmd_Click);
            ToolStripMenuItem miProps = new ToolStripMenuItem("Properties..."); miProps.Click += new EventHandler(miProps_Click);
            ToolStripMenuItem miDelete = new ToolStripMenuItem("Delete (Recycle Bin)"); miDelete.Click += new EventHandler(miDelete_Click);
            itemMenu.Items.Add(miOpen);
            itemMenu.Items.Add(miDrill);
            itemMenu.Items.Add(miCopy);
            itemMenu.Items.Add(miCmd);
            itemMenu.Items.Add(new ToolStripSeparator());
            itemMenu.Items.Add(miProps);
            itemMenu.Items.Add(new ToolStripSeparator());
            itemMenu.Items.Add(miDelete);
            list.ContextMenuStrip = itemMenu;

            split.Panel2.Controls.Add(list);
            split.Panel2.Controls.Add(breadcrumbBar);

            // ---- Status ----
            status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            statusLabel.Text = "Ready. Click a drive on the left to scan.";
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            sizeLabel = new ToolStripStatusLabel();
            sizeLabel.Text = "Size: -"; sizeLabel.AutoSize = false; sizeLabel.Width = 160;
            sizeLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            countLabel = new ToolStripStatusLabel();
            countLabel.Text = "Files: 0"; countLabel.AutoSize = false; countLabel.Width = 140;
            countLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            timeLabel = new ToolStripStatusLabel();
            timeLabel.Text = "0.0 s"; timeLabel.AutoSize = false; timeLabel.Width = 80;
            timeLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            progress = new ThemedProgressBar();
            progress.Style = ProgressBarStyle.Continuous;
            progress.Width = 160;
            progress.Height = 16;
            progress.Maximum = 1000;
            progressHost = new ToolStripControlHost(progress);
            progressHost.AutoSize = false;
            progressHost.Width = 160;
            progressHost.Margin = new Padding(2, 3, 4, 3);

            pctLabel = new ToolStripStatusLabel();
            pctLabel.Text = "";
            pctLabel.AutoSize = false;
            pctLabel.Width = 60;
            pctLabel.TextAlign = ContentAlignment.MiddleLeft;
            pctLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            pctLabel.ForeColor = Color.FromArgb(40, 100, 200);

            status.Items.Add(progressHost);
            status.Items.Add(pctLabel);
            status.Items.Add(statusLabel);
            status.Items.Add(sizeLabel);
            status.Items.Add(countLabel);
            status.Items.Add(timeLabel);

            // ---- Main menu (Help) + Struis ICT brand mark on the right ----
            mainMenu = new MenuStrip();
            ToolStripMenuItem viewMenu = new ToolStripMenuItem("&View");
            ToolStripMenuItem themeMenu = new ToolStripMenuItem("&Theme");
            themeAutoItem = new ToolStripMenuItem("&Auto (follow Windows)");
            themeLightItem = new ToolStripMenuItem("&Light");
            themeDarkItem = new ToolStripMenuItem("&Dark");
            themeAutoItem.Click += new EventHandler(themeAuto_Click);
            themeLightItem.Click += new EventHandler(themeLight_Click);
            themeDarkItem.Click += new EventHandler(themeDark_Click);
            themeMenu.DropDownItems.Add(themeAutoItem);
            themeMenu.DropDownItems.Add(themeLightItem);
            themeMenu.DropDownItems.Add(themeDarkItem);
            viewMenu.DropDownItems.Add(themeMenu);
            UpdateThemeMenuChecks();
            mainMenu.Items.Add(viewMenu);

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
            ToolStripMenuItem miAbout = new ToolStripMenuItem("&About ClutterCutter...");
            miAbout.Click += new EventHandler(helpAbout_Click);
            ToolStripMenuItem miKeys = new ToolStripMenuItem("&Keyboard shortcuts");
            miKeys.Click += new EventHandler(helpKeys_Click);
            ToolStripMenuItem miCoffee = new ToolStripMenuItem("Buy me a &coffee ☕");
            miCoffee.Click += new EventHandler(helpCoffee_Click);
            helpMenu.DropDownItems.Add(miAbout);
            helpMenu.DropDownItems.Add(miKeys);
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(miCoffee);
            mainMenu.Items.Add(helpMenu);

            // Right-aligned brand: separator + clickable Struis ICT label that opens the About dialog.
            ToolStripSeparator brandSep = new ToolStripSeparator();
            brandSep.Alignment = ToolStripItemAlignment.Right;
            ToolStripLabel brandLabel = new ToolStripLabel("Struis ICT");
            brandLabel.Alignment = ToolStripItemAlignment.Right;
            brandLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            brandLabel.ForeColor = Color.FromArgb(40, 100, 200);
            brandLabel.IsLink = true;
            brandLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            brandLabel.LinkColor = Color.FromArgb(40, 100, 200);
            brandLabel.ActiveLinkColor = Color.FromArgb(20, 60, 160);
            brandLabel.ToolTipText = "Built and maintained by Struis ICT — click for About";
            brandLabel.Click += new EventHandler(helpAbout_Click);
            brandLabelRef = brandLabel;
            mainMenu.Items.Add(brandSep);
            mainMenu.Items.Add(brandLabel);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(status);
            toolbar.Dock = DockStyle.Top;
            status.Dock = DockStyle.Bottom;
            // Add menu LAST so its Dock=Top wins (and it sits above the toolbar).
            this.MainMenuStrip = mainMenu;
            Controls.Add(mainMenu);
            mainMenu.Dock = DockStyle.Top;

            elapsedTimer = new System.Windows.Forms.Timer();
            elapsedTimer.Interval = 100;
            elapsedTimer.Tick += new EventHandler(elapsedTimer_Tick);
        }

        void elapsedTimer_Tick(object sender, EventArgs e)
        {
            if (scanning)
            {
                TimeSpan ts = DateTime.UtcNow - scanStart;
                timeLabel.Text = ts.TotalSeconds.ToString("F1") + " s";
            }
        }

        void LoadDrives()
        {
            drivePanel.SuspendLayout();
            drivePanel.Controls.Clear();
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                int cardWidth = drivePanel.ClientSize.Width - drivePanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
                if (cardWidth < 200) cardWidth = 280;

                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo d = drives[i];
                    DriveCard card = new DriveCard();
                    card.Drive = d;
                    card.Width = cardWidth;
                    if (d.IsReady)
                    {
                        try
                        {
                            string drvLabel = string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel;
                            card.LabelLine1 = string.Format("{0}   {1}", d.Name.TrimEnd('\\'), drvLabel);
                            card.UsedBytes = d.TotalSize - d.AvailableFreeSpace;
                            card.TotalBytes = d.TotalSize;
                            card.LabelLine2 = string.Format("{0} free of {1}  ({2:F0}% used)",
                                Sizes.Format(d.AvailableFreeSpace),
                                Sizes.Format(d.TotalSize),
                                100.0 * card.UsedBytes / Math.Max(1, d.TotalSize));
                        }
                        catch
                        {
                            card.LabelLine1 = d.Name;
                            card.LabelLine2 = "(info unavailable)";
                        }
                    }
                    else
                    {
                        card.LabelLine1 = d.Name;
                        card.LabelLine2 = "(not ready)";
                        card.Enabled = false;
                    }
                    card.Click += new EventHandler(driveCard_Click);
                    drivePanel.Controls.Add(card);
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Error loading drives: " + ex.Message;
            }
            drivePanel.ResumeLayout();
        }

        void drivePanel_SizeChanged(object sender, EventArgs e) { ResizeDriveCards(); }
        void drivePanel_Layout(object sender, LayoutEventArgs e) { ResizeDriveCards(); }

        void ResizeDriveCards()
        {
            if (drivePanel == null) return;
            int avail = drivePanel.ClientSize.Width - drivePanel.Padding.Horizontal;
            if (avail < 80) return;
            for (int i = 0; i < drivePanel.Controls.Count; i++)
            {
                Control c = drivePanel.Controls[i];
                if (!(c is DriveCard)) continue;
                int target = avail - c.Margin.Horizontal;
                if (c.Width != target) c.Width = target;
            }
        }

        void driveCard_Click(object sender, EventArgs e)
        {
            DriveCard card = sender as DriveCard;
            if (card == null || card.Drive == null) return;
            if (selectedCard != null) { selectedCard.IsSelected = false; selectedCard.Invalidate(); }
            selectedCard = card; card.IsSelected = true; card.Invalidate();
            string target = card.Drive.Name;
            customPathBox.Text = target;
            StartScan(target);
        }

        void browseBtn_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Pick a folder to scan";
            dlg.ShowNewFolderButton = false;
            try
            {
                string cur = customPathBox.Text == null ? "" : customPathBox.Text.Trim();
                if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur)) dlg.SelectedPath = cur;
            }
            catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                customPathBox.Text = dlg.SelectedPath;
                StartScan(dlg.SelectedPath);
            }
        }

        void customPathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                StartScan(customPathBox.Text);
            }
        }

        void scanBtn_Click(object sender, EventArgs e)
        {
            string t = (customPathBox.Text == null ? "" : customPathBox.Text).Trim();
            if (string.IsNullOrEmpty(t))
            {
                if (selectedCard != null && selectedCard.Drive != null) t = selectedCard.Drive.Name;
            }
            if (string.IsNullOrEmpty(t))
            {
                MessageBox.Show(this, "Click a drive on the left, or enter a path in 'Custom path' and press Scan.",
                    "ClutterCutter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StartScan(t);
        }

        void stopBtn_Click(object sender, EventArgs e) { DoStop(); }
        void refreshBtn_Click(object sender, EventArgs e) { DoRefresh(); }
        void parentBtn_Click(object sender, EventArgs e) { GoToParent(); }
        void openBtn_Click(object sender, EventArgs e) { if (selectedNode != null) OpenInExplorer(selectedNode.FullPath); }

        void helpAbout_Click(object sender, EventArgs e) { ShowAboutDialog(); }

        const string CoffeeUrl = "https://buymeacoffee.com/struis112";
        void helpCoffee_Click(object sender, EventArgs e) { OpenUrl(CoffeeUrl); }
        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        void themeAuto_Click(object sender, EventArgs e)  { SetThemeMode(ThemeMode.Auto); }
        void themeLight_Click(object sender, EventArgs e) { SetThemeMode(ThemeMode.Light); }
        void themeDark_Click(object sender, EventArgs e)  { SetThemeMode(ThemeMode.Dark); }

        void SetThemeMode(ThemeMode m)
        {
            Theme.Mode = m;
            Theme.Refresh();
            UpdateThemeMenuChecks();
            ApplyTheme();
            SaveThemeMode(m);
        }

        void UpdateThemeMenuChecks()
        {
            if (themeAutoItem == null) return;
            themeAutoItem.Checked = (Theme.Mode == ThemeMode.Auto);
            themeLightItem.Checked = (Theme.Mode == ThemeMode.Light);
            themeDarkItem.Checked = (Theme.Mode == ThemeMode.Dark);
        }

        static string ThemeFilePath()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                return Path.Combine(dir, "theme.cfg");
            }
            catch { return null; }
        }

        // Default = Auto (follow OS) when no preference is saved.
        public static ThemeMode LoadThemeMode()
        {
            try
            {
                string p = ThemeFilePath();
                if (p == null || !File.Exists(p)) return ThemeMode.Auto;
                string s = File.ReadAllText(p).Trim().ToLowerInvariant();
                if (s == "dark") return ThemeMode.Dark;
                if (s == "light") return ThemeMode.Light;
            }
            catch { }
            return ThemeMode.Auto;
        }

        static void SaveThemeMode(ThemeMode m)
        {
            try
            {
                string p = ThemeFilePath();
                if (p != null) File.WriteAllText(p, m == ThemeMode.Dark ? "dark" : m == ThemeMode.Light ? "light" : "auto");
            }
            catch { }
        }

        void ApplyTheme()
        {
            this.SuspendLayout();
            try
            {
                ToolStripProfessionalRenderer renderer = new ThemedRenderer();
                ToolStripManager.Renderer = renderer;
                if (toolbar != null) toolbar.Renderer = renderer;
                if (breadcrumbBar != null) breadcrumbBar.Renderer = renderer;
                if (status != null) status.Renderer = renderer;
                if (mainMenu != null) mainMenu.Renderer = renderer;

                this.BackColor = Theme.FormBg;
                this.ForeColor = Theme.Text;
                if (split != null) { split.Panel1.BackColor = Theme.FormBg; split.Panel2.BackColor = Theme.FormBg; }
                if (leftPanel != null) leftPanel.BackColor = Theme.FormBg;
                if (drivesHeader != null) { drivesHeader.BackColor = Theme.HeaderBg; drivesHeader.ForeColor = Theme.Text; }
                if (foldersHeader != null) { foldersHeader.BackColor = Theme.HeaderBg; foldersHeader.ForeColor = Theme.Text; }
                if (drivePanel != null) drivePanel.BackColor = Theme.PanelBg;
                if (breadcrumbBar != null) breadcrumbBar.BackColor = Theme.HeaderBg;

                if (tree != null) { tree.BackColor = Theme.CtrlBg; tree.ForeColor = Theme.Text; tree.LineColor = Theme.SubText; }
                if (list != null) { list.BackColor = Theme.CtrlBg; list.ForeColor = Theme.Text; }
                if (customPathBox != null) { customPathBox.BackColor = Theme.CtrlAltBg; customPathBox.ForeColor = Theme.Text; }

                if (brandLabelRef != null)
                {
                    brandLabelRef.ForeColor = Theme.Accent;
                    brandLabelRef.LinkColor = Theme.Accent;
                }
                if (pctLabel != null) pctLabel.ForeColor = Theme.Accent;

                // Native bits that ignore BackColor: title bar (DWM) and scroll bars (UxTheme).
                NativeTheme.EnsureAppMode(Theme.Dark);
                if (this.IsHandleCreated) NativeTheme.ApplyTitleBar(this.Handle, Theme.Dark);
                if (tree != null && tree.IsHandleCreated) NativeTheme.ApplyControlTheme(tree.Handle, Theme.Dark);
                if (list != null && list.IsHandleCreated) NativeTheme.ApplyControlTheme(list.Handle, Theme.Dark);

                // Repaint owner-drawn cards/list rows
                if (drivePanel != null)
                {
                    foreach (Control c in drivePanel.Controls) c.Invalidate();
                }
                if (list != null) list.Invalidate();
                if (tree != null) tree.Invalidate();
                Theme.RaiseChanged();
            }
            finally { this.ResumeLayout(); }
        }
        void helpKeys_Click(object sender, EventArgs e)
        {
            string msg =
                "Keyboard shortcuts\n\n" +
                "F5\t\tRefresh / re-scan current target\n" +
                "Esc\t\tStop the running scan\n" +
                "Backspace\t\tGo to parent folder (when tree has focus)\n" +
                "Enter\t\tDrill into the selected list row\n" +
                "Del\t\tDelete selected items to Recycle Bin\n" +
                "Right-click\tContext menu (Open, Copy path, Properties...)\n";
            MessageBox.Show(this, msg, "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowAboutDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "About ClutterCutter";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(520, 410);
                dlg.BackColor = Theme.FormBg;
                dlg.ForeColor = Theme.Text;

                Label title = new Label();
                title.Text = "ClutterCutter";
                title.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
                title.AutoSize = true;
                title.Location = new Point(20, 18);

                Label tagline = new Label();
                tagline.Text = "A lightweight disk-usage browser for Windows.";
                tagline.Font = new Font("Segoe UI", 9.5F, FontStyle.Italic);
                tagline.ForeColor = Color.DimGray;
                tagline.AutoSize = true;
                tagline.Location = new Point(22, 56);

                Label disclaimer = new Label();
                disclaimer.Text =
                    "Built and maintained by Struis ICT.\n\n" +
                    "This tool is provided as-is, without warranty of any kind.\n" +
                    "It reads file metadata only and does not transmit data over the network.\n" +
                    "Deleting files is performed via the Windows Recycle Bin.\n\n" +
                    "© Struis ICT — all rights reserved.";
                disclaimer.Font = new Font("Segoe UI", 9.5F);
                disclaimer.AutoSize = false;
                disclaimer.Size = new Size(480, 130);
                disclaimer.Location = new Point(22, 90);

                Label tech = new Label();
                tech.Text =
                    "Built in C# (.NET Framework 4) — single-file native exe, no installer.\n" +
                    "Uses the Win32 NTFS Master File Table for fast drive scans where possible.";
                tech.Font = new Font("Segoe UI", 8.5F);
                tech.ForeColor = Color.FromArgb(90, 90, 90);
                tech.AutoSize = false;
                tech.Size = new Size(480, 40);
                tech.Location = new Point(22, 230);

                Label support = new Label();
                support.Text = "Enjoying ClutterCutter? You can support development:";
                support.Font = new Font("Segoe UI", 9.5F);
                support.ForeColor = Theme.SubText;
                support.AutoSize = true;
                support.Location = new Point(22, 280);

                LinkLabel coffeeLink = new LinkLabel();
                coffeeLink.Text = "☕  Buy me a coffee  →  buymeacoffee.com/struis112";
                coffeeLink.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
                coffeeLink.LinkColor = Theme.Accent;
                coffeeLink.ActiveLinkColor = Theme.Accent;
                coffeeLink.VisitedLinkColor = Theme.Accent;
                coffeeLink.LinkBehavior = LinkBehavior.HoverUnderline;
                coffeeLink.AutoSize = true;
                coffeeLink.Location = new Point(22, 305);
                coffeeLink.Click += new EventHandler(delegate(object s, EventArgs ev) { OpenUrl(CoffeeUrl); });

                Button ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(90, 28);
                ok.Location = new Point(dlg.ClientSize.Width - 110, dlg.ClientSize.Height - 42);
                ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

                dlg.Controls.Add(title);
                dlg.Controls.Add(tagline);
                dlg.Controls.Add(disclaimer);
                dlg.Controls.Add(tech);
                dlg.Controls.Add(support);
                dlg.Controls.Add(coffeeLink);
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;
                dlg.CancelButton = ok;
                dlg.ShowDialog(this);
            }
        }

        void topBtn_CheckedChanged(object sender, EventArgs e)
        {
            topMode = topBtn.Checked;
            if (selectedNode != null) PopulateList(selectedNode);
        }

        void DoRefresh()
        {
            if (string.IsNullOrEmpty(lastScannedPath)) scanBtn_Click(null, null);
            else StartScan(lastScannedPath);
        }

        void DoStop() { if (cts != null) { try { cts.Cancel(); } catch { } } }

        void GoToParent()
        {
            if (tree.SelectedNode != null && tree.SelectedNode.Parent != null)
                tree.SelectedNode = tree.SelectedNode.Parent;
        }

        void StartScan(string targetRaw)
        {
            if (scanning) return;
            string target = (targetRaw == null ? "" : targetRaw).Trim();
            if (target.Length == 2 && target[1] == ':') target += "\\";
            if (string.IsNullOrEmpty(target)) return;
            if (!Directory.Exists(target))
            {
                MessageBox.Show(this, "Path does not exist or is not accessible:\n" + target,
                    "ClutterCutter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            lastScannedPath = target;
            scanning = true;
            scanStart = DateTime.UtcNow;
            scanBtn.Enabled = false;
            stopBtn.Enabled = true;
            refreshBtn.Enabled = false;
            tree.Nodes.Clear();
            list.Items.Clear();
            ClearBreadcrumb();
            sizeLabel.Text = "Size: scanning...";
            countLabel.Text = "Files: 0";
            statusLabel.Text = "Scanning " + target;
            progress.Visible = true;
            progress.Style = ProgressBarStyle.Marquee;
            progress.Value = 0;
            pctLabel.Text = "";
            elapsedTimer.Start();

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            Progress<ScanProgress> prog = new Progress<ScanProgress>(OnProgress);

            bool tryMft = Program.IsAdmin && MftScanner.IsNtfsDriveRoot(target);
            string captured = target;
            scanModeUsed = tryMft ? "MFT" : "FindFirstFile";
            statusLabel.Text = (tryMft ? "MFT fast path: " : "Scanning ") + target;

            Task.Factory.StartNew(new Func<FolderNode>(delegate
            {
                if (tryMft)
                {
                    try
                    {
                        MftScanner ms = new MftScanner();
                        ms.Token = token;
                        ms.Progress = prog;
                        FolderNode r = ms.Scan(captured);
                        return r;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Program.Log("MFT failed, falling back: " + ex.Message);
                        scanModeUsed = "FindFirstFile (MFT failed: " + ex.Message + ")";
                    }
                }
                Scanner s = new Scanner();
                s.Token = token;
                s.Progress = prog;
                // For drive roots, the volume's "used space" is a good % yardstick. For subfolders we
                // can't pre-compute a target; leave hint=0 so the bar stays indeterminate.
                s.TotalSizeHint = TryGetDriveUsedSize(captured);
                return s.Scan(captured);
            }), token).ContinueWith(new Action<Task<FolderNode>>(delegate(Task<FolderNode> t)
            {
                this.BeginInvoke(new Action(delegate { OnScanComplete(t); }));
            }));
        }

        // Returns the drive's used-bytes (TotalSize - AvailableFreeSpace) when path is a drive root.
        // Returns 0 otherwise (which makes the progress bar stay indeterminate for subfolder scans).
        static long TryGetDriveUsedSize(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') return 0;
                string root = path.Substring(0, 2) + "\\";
                if (path.TrimEnd('\\').Length > 2) return 0; // subfolder, not a drive root
                DriveInfo di = new DriveInfo(root);
                if (!di.IsReady) return 0;
                return Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
            }
            catch { return 0; }
        }

        void OnProgress(ScanProgress p)
        {
            sizeLabel.Text = "Size: " + Sizes.Format(p.TotalSize);
            countLabel.Text = "Files: " + Sizes.FormatCount(p.FilesScanned);
            string trimmed = p.CurrentPath;
            if (trimmed != null && trimmed.Length > 90) trimmed = "..." + trimmed.Substring(trimmed.Length - 87);
            statusLabel.Text = "Scanning: " + trimmed;

            if (p.Percent >= 0)
            {
                if (progress.Style != ProgressBarStyle.Continuous) progress.Style = ProgressBarStyle.Continuous;
                int v = (int)Math.Round(p.Percent * 10);
                if (v < 0) v = 0; if (v > 1000) v = 1000;
                progress.Value = v;
                pctLabel.Text = p.Percent.ToString("F1") + "%";
            }
            else
            {
                if (progress.Style != ProgressBarStyle.Marquee) progress.Style = ProgressBarStyle.Marquee;
                pctLabel.Text = "";
            }
        }

        void OnScanComplete(Task<FolderNode> t)
        {
            scanning = false;
            scanBtn.Enabled = true;
            stopBtn.Enabled = false;
            refreshBtn.Enabled = true;
            // Keep the bar visible; just reset to empty so it looks the same as before scanning.
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;
            pctLabel.Text = "";
            elapsedTimer.Stop();
            TimeSpan elapsed = DateTime.UtcNow - scanStart;
            timeLabel.Text = elapsed.TotalSeconds.ToString("F1") + " s";

            if (t.IsFaulted)
            {
                statusLabel.Text = "Scan failed: " + (t.Exception != null ? t.Exception.GetBaseException().Message : "unknown");
                return;
            }
            if (t.IsCanceled || (t.Exception != null && HasCancellation(t.Exception)))
            {
                statusLabel.Text = "Scan cancelled.";
                if (t.Result != null) PopulateAfterScan(t.Result);
                return;
            }

            rootNode = t.Result;
            PopulateAfterScan(rootNode);
            statusLabel.Text = string.Format("Done [{4}]. {0} files, {1} folders, {2} total in {3:F1} s. Click a row to drill in.",
                Sizes.FormatCount(rootNode.FileCount), Sizes.FormatCount(rootNode.FolderCount),
                Sizes.Format(rootNode.Size), elapsed.TotalSeconds, scanModeUsed);
            sizeLabel.Text = "Size: " + Sizes.Format(rootNode.Size);
            countLabel.Text = "Files: " + Sizes.FormatCount(rootNode.FileCount);
        }

        bool HasCancellation(Exception ex)
        {
            if (ex == null) return false;
            if (ex is OperationCanceledException) return true;
            AggregateException ag = ex as AggregateException;
            if (ag != null) for (int i = 0; i < ag.InnerExceptions.Count; i++) if (HasCancellation(ag.InnerExceptions[i])) return true;
            return HasCancellation(ex.InnerException);
        }

        void PopulateAfterScan(FolderNode root)
        {
            if (root == null) return;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode tn = MakeTreeNode(root);
            tree.Nodes.Add(tn);
            ExpandWithChildren(tn);
            tn.Expand();
            tree.SelectedNode = tn;
            tree.EndUpdate();
            tree.Focus();
        }

        TreeNode MakeTreeNode(FolderNode f)
        {
            string label = f.Name + "   (" + Sizes.Format(f.Size) + ")";
            if (f.IsAccessDenied) label += "  [access denied]";
            TreeNode tn = new TreeNode(label);
            tn.Tag = f;
            if (f.Children.Count > 0) { TreeNode placeholder = new TreeNode("..."); placeholder.Tag = null; tn.Nodes.Add(placeholder); }
            return tn;
        }

        void ExpandWithChildren(TreeNode tn)
        {
            FolderNode f = tn.Tag as FolderNode;
            if (f == null) return;
            tn.Nodes.Clear();
            List<FolderNode> sorted = new List<FolderNode>(f.Children);
            sorted.Sort(new Comparison<FolderNode>(delegate(FolderNode a, FolderNode b) { return b.Size.CompareTo(a.Size); }));
            for (int i = 0; i < sorted.Count; i++) tn.Nodes.Add(MakeTreeNode(sorted[i]));
        }

        void tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            FolderNode f = e.Node.Tag as FolderNode;
            if (f == null) return;
            if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag == null) ExpandWithChildren(e.Node);
        }

        void tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            FolderNode f = e.Node == null ? null : e.Node.Tag as FolderNode;
            selectedNode = f;
            UpdateBreadcrumb(f);
            if (f == null) { list.Items.Clear(); return; }
            PopulateList(f);
        }

        void tree_NodeDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            FolderNode f = e.Node == null ? null : e.Node.Tag as FolderNode;
            if (f != null) OpenInExplorer(f.FullPath);
        }

        void ClearBreadcrumb()
        {
            breadcrumbBar.Items.Clear();
        }

        void UpdateBreadcrumb(FolderNode f)
        {
            breadcrumbBar.Items.Clear();
            if (f == null) return;

            // Walk parents up to root
            List<FolderNode> chain = new List<FolderNode>();
            FolderNode cur = f;
            while (cur != null) { chain.Insert(0, cur); cur = cur.Parent; }

            for (int i = 0; i < chain.Count; i++)
            {
                FolderNode node = chain[i];
                ToolStripButton seg = new ToolStripButton(node.Name);
                seg.Tag = node;
                seg.DisplayStyle = ToolStripItemDisplayStyle.Text;
                seg.AutoSize = true;
                seg.Margin = new Padding(0, 2, 0, 2);
                if (i == chain.Count - 1) seg.Font = new Font(breadcrumbBar.Font, FontStyle.Bold);
                seg.Click += new EventHandler(breadcrumb_Click);
                breadcrumbBar.Items.Add(seg);
                if (i < chain.Count - 1)
                {
                    ToolStripLabel sep = new ToolStripLabel(" › ");
                    sep.ForeColor = Color.Gray;
                    breadcrumbBar.Items.Add(sep);
                }
            }
        }

        void breadcrumb_Click(object sender, EventArgs e)
        {
            ToolStripButton b = sender as ToolStripButton;
            if (b == null) return;
            FolderNode target = b.Tag as FolderNode;
            if (target == null) return;
            SelectInTree(target);
        }

        // Find the TreeNode whose Tag is the given FolderNode and select it (lazy-expanding parents).
        void SelectInTree(FolderNode target)
        {
            if (target == null || tree.Nodes.Count == 0) return;
            // Build chain from root to target
            List<FolderNode> chain = new List<FolderNode>();
            FolderNode cur = target;
            while (cur != null) { chain.Insert(0, cur); cur = cur.Parent; }

            TreeNode current = tree.Nodes[0];
            if (current.Tag != chain[0]) return;
            for (int i = 1; i < chain.Count; i++)
            {
                if (current.Nodes.Count == 1 && current.Nodes[0].Tag == null) ExpandWithChildren(current);
                current.Expand();
                TreeNode found = null;
                for (int j = 0; j < current.Nodes.Count; j++)
                {
                    if (current.Nodes[j].Tag == chain[i]) { found = current.Nodes[j]; break; }
                }
                if (found == null) break;
                current = found;
            }
            tree.SelectedNode = current;
            current.EnsureVisible();
        }

        void PopulateList(FolderNode f)
        {
            list.BeginUpdate();
            list.Items.Clear();

            if (topMode)
            {
                BuildTopList(f);
            }
            else
            {
                BuildChildrenList(f);
            }

            list.EndUpdate();
            Text = "ClutterCutter — " + f.FullPath + "  (" + Sizes.Format(f.Size) + ")";
        }

        void BuildChildrenList(FolderNode f)
        {
            long parentSize = f.Size > 0 ? f.Size : 1;
            List<FolderNode> children = new List<FolderNode>(f.Children);
            children.Sort(new Comparison<FolderNode>(delegate(FolderNode a, FolderNode b)
            {
                int cmp;
                switch (sortColumn)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1:
                    case 2: cmp = a.Size.CompareTo(b.Size); break;
                    case 3: cmp = a.OwnSize.CompareTo(b.OwnSize); break;
                    case 4: cmp = a.FileCount.CompareTo(b.FileCount); break;
                    case 5: cmp = a.FolderCount.CompareTo(b.FolderCount); break;
                    case 6: cmp = a.LastModified.CompareTo(b.LastModified); break;
                    default: cmp = a.Size.CompareTo(b.Size); break;
                }
                return sortDesc ? -cmp : cmp;
            }));

            for (int i = 0; i < children.Count; i++)
            {
                FolderNode c = children[i];
                AddRow(c, c.Name + (c.IsAccessDenied ? "  [access denied]" : ""), c.Size, c.OwnSize, c.FileCount, c.FolderCount, c.LastModified, parentSize);
            }
            if (f.OwnSize > 0)
            {
                ListViewItem item = new ListViewItem("<Files directly in folder>");
                item.ForeColor = Color.DarkSlateBlue;
                ListViewItem.ListViewSubItem pctSub = new ListViewItem.ListViewSubItem();
                double pct = (double)f.OwnSize / parentSize;
                pctSub.Text = (pct * 100).ToString("F1") + "%";
                pctSub.Tag = pct.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                item.SubItems.Add(pctSub);
                item.SubItems.Add(Sizes.Format(f.OwnSize));
                item.SubItems.Add(Sizes.Format(f.OwnSize));
                item.SubItems.Add(Sizes.FormatCount(f.DirectFileCount));
                item.SubItems.Add("0");
                item.SubItems.Add("");
                item.Tag = null;
                list.Items.Add(item);
            }
        }

        // Top mode: flatten ALL descendant folders of f and show the largest, regardless of depth.
        void BuildTopList(FolderNode f)
        {
            long parentSize = f.Size > 0 ? f.Size : 1;
            List<FolderNode> all = new List<FolderNode>();
            CollectDescendants(f, all);
            // Always sort by size desc in top mode (it's the whole point), but allow column override
            int sc = sortColumn; bool sd = sortDesc;
            if (sc == 0) sd = sortDesc; // name still respects user click
            all.Sort(new Comparison<FolderNode>(delegate(FolderNode a, FolderNode b)
            {
                int cmp;
                switch (sc)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1:
                    case 2: cmp = a.Size.CompareTo(b.Size); break;
                    case 3: cmp = a.OwnSize.CompareTo(b.OwnSize); break;
                    case 4: cmp = a.FileCount.CompareTo(b.FileCount); break;
                    case 5: cmp = a.FolderCount.CompareTo(b.FolderCount); break;
                    case 6: cmp = a.LastModified.CompareTo(b.LastModified); break;
                    default: cmp = a.Size.CompareTo(b.Size); break;
                }
                return sd ? -cmp : cmp;
            }));
            int max = Math.Min(200, all.Count);
            string rootPath = f.FullPath;
            for (int i = 0; i < max; i++)
            {
                FolderNode c = all[i];
                string rel = c.FullPath;
                if (rel != null && rootPath != null && rel.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel.Substring(rootPath.Length).TrimStart('\\', '/');
                    if (string.IsNullOrEmpty(rel)) rel = c.Name;
                }
                AddRow(c, rel + (c.IsAccessDenied ? "  [access denied]" : ""), c.Size, c.OwnSize, c.FileCount, c.FolderCount, c.LastModified, parentSize);
            }
        }

        void CollectDescendants(FolderNode root, List<FolderNode> bucket)
        {
            if (root == null) return;
            for (int i = 0; i < root.Children.Count; i++)
            {
                bucket.Add(root.Children[i]);
                CollectDescendants(root.Children[i], bucket);
            }
        }

        void AddRow(FolderNode tag, string name, long size, long ownSize, long files, long folders, DateTime modified, long parentSize)
        {
            ListViewItem item = new ListViewItem(name);
            ListViewItem.ListViewSubItem pctSub = new ListViewItem.ListViewSubItem();
            double pct = parentSize > 0 ? (double)size / parentSize : 0;
            pctSub.Text = (pct * 100).ToString("F1") + "%";
            pctSub.Tag = pct.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            item.SubItems.Add(pctSub);
            item.SubItems.Add(Sizes.Format(size));
            item.SubItems.Add(Sizes.Format(ownSize));
            item.SubItems.Add(Sizes.FormatCount(files));
            item.SubItems.Add(Sizes.FormatCount(folders));
            string md = modified == DateTime.MinValue ? "" : modified.ToString("yyyy-MM-dd HH:mm");
            item.SubItems.Add(md);
            item.Tag = tag;
            if (tag != null && tag.IsAccessDenied) item.ForeColor = Color.DarkRed;
            list.Items.Add(item);
        }

        void list_DoubleClick(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0) return;
            FolderNode f = list.SelectedItems[0].Tag as FolderNode;
            if (f != null) SelectInTree(f);
        }

        void list_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn == e.Column) sortDesc = !sortDesc;
            else { sortColumn = e.Column; sortDesc = (e.Column != 0); }
            if (selectedNode != null) PopulateList(selectedNode);
        }

        void list_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && list.SelectedItems.Count > 0)
            {
                list_DoubleClick(sender, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && list.SelectedItems.Count > 0)
            {
                miDelete_Click(sender, EventArgs.Empty);
                e.Handled = true;
            }
        }

        void miOpen_Click(object sender, EventArgs e)
        {
            FolderNode f = GetClickedFolder();
            if (f != null) OpenInExplorer(f.FullPath);
        }
        void miDrill_Click(object sender, EventArgs e)
        {
            FolderNode f = GetClickedFolder();
            if (f != null) SelectInTree(f);
        }
        void miCopy_Click(object sender, EventArgs e)
        {
            FolderNode f = GetClickedFolder();
            if (f != null) { try { Clipboard.SetText(f.FullPath); statusLabel.Text = "Copied: " + f.FullPath; } catch { } }
        }
        void miCmd_Click(object sender, EventArgs e)
        {
            FolderNode f = GetClickedFolder();
            if (f == null) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe");
                psi.WorkingDirectory = f.FullPath; psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open command prompt failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void miProps_Click(object sender, EventArgs e)
        {
            FolderNode f = GetClickedFolder();
            if (f == null) return;
            try { ShellPropertiesDialog(f.FullPath); } catch { }
        }
        void miDelete_Click(object sender, EventArgs e)
        {
            List<FolderNode> targets = new List<FolderNode>();
            for (int i = 0; i < list.SelectedItems.Count; i++)
            {
                FolderNode f = list.SelectedItems[i].Tag as FolderNode;
                if (f != null) targets.Add(f);
            }
            if (targets.Count == 0) return;
            string msg = "Move the following to the Recycle Bin?\n\n";
            for (int i = 0; i < Math.Min(8, targets.Count); i++) msg += "• " + targets[i].FullPath + "\n";
            if (targets.Count > 8) msg += "...and " + (targets.Count - 8) + " more\n";
            if (MessageBox.Show(this, msg, "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int ok = 0, fail = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        targets[i].FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ok++;
                }
                catch { fail++; }
            }
            statusLabel.Text = string.Format("Deleted {0} item(s); {1} failed. Press F5 to refresh.", ok, fail);
        }

        FolderNode GetClickedFolder()
        {
            if (list.SelectedItems.Count > 0)
            {
                FolderNode f = list.SelectedItems[0].Tag as FolderNode;
                if (f != null) return f;
            }
            return selectedNode;
        }

        static void OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        static void ShellPropertiesDialog(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = path; psi.Verb = "properties"; psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
    }

    static class Program
    {
        public static string LogPath;

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); }
            catch { }
        }

        public static bool IsAdmin;

        public static bool IsRunningAsAdmin()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal pr = new System.Security.Principal.WindowsPrincipal(id);
                    return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // Relaunch self with the "runas" verb (UAC elevation). Returns true if launch succeeded.
        public static bool RelaunchAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = typeof(Program).Assembly.Location;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "--from-elevation";
                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // user clicked No on UAC
            catch { return false; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                LogPath = Path.Combine(dir, "ClutterCutter.log");
            }
            catch { LogPath = Path.Combine(Path.GetTempPath(), "ClutterCutter.log"); }
            try { File.WriteAllText(LogPath, ""); } catch { }

            IsAdmin = IsRunningAsAdmin();
            Log("Boot. .NET=" + Environment.Version + " OS=" + Environment.OSVersion + " 64bit=" + Environment.Is64BitProcess + " admin=" + IsAdmin);

            // Offer elevation if we're not admin and weren't just spawned from a UAC prompt.
            bool fromElevation = false;
            for (int i = 0; args != null && i < args.Length; i++) if (args[i] == "--from-elevation") fromElevation = true;
            if (!IsAdmin && !fromElevation)
            {
                string msg =
                    "ClutterCutter is not running as Administrator.\n\n" +
                    "Without admin rights:\n" +
                    "  • The fast NTFS Master File Table path is unavailable.\n" +
                    "  • Some system folders (e.g. \"System Volume Information\", per-user profiles) cannot be read.\n\n" +
                    "Click Yes to relaunch with elevation, No to continue without admin.";
                DialogResult dr = MessageBox.Show(msg, "ClutterCutter — Elevation recommended",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel) return;
                if (dr == DialogResult.Yes)
                {
                    if (RelaunchAsAdmin()) return; // elevated copy will run; this one exits
                    Log("Elevation declined or failed; continuing without admin.");
                }
            }

            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("AppDomain unhandled: " + (e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString()));
            });
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Log("Application.ThreadException: " + e.Exception.ToString());
                try { MessageBox.Show("Error: " + e.Exception.Message + "\n\nLog: " + LogPath, "ClutterCutter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            });

            try
            {
                Log("EnableVisualStyles");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Opt the process into dark scroll bars / list separators if the saved theme is dark.
                // Must be called before any HWND is created.
                Theme.Mode = MainForm.LoadThemeMode();
                Theme.Refresh();
                NativeTheme.EnsureAppMode(Theme.Dark);
                Log("Constructing MainForm");
                MainForm f = new MainForm();
                Log("MainForm constructed");
                f.Shown += new EventHandler(delegate(object s, EventArgs e) { Log("Form Shown event fired"); });
                Log("Application.Run starting");
                Application.Run(f);
                Log("Application.Run returned");
            }
            catch (Exception ex)
            {
                Log("FATAL during startup: " + ex.ToString());
                try { MessageBox.Show("Fatal: " + ex.ToString() + "\n\nLog: " + LogPath, "ClutterCutter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            }
        }
    }
}
