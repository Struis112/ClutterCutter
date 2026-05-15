// NTFS Master File Table scanner — direct port of MftScanner in ClutterCutter.cs.
// Opens \\.\<drive>: (requires admin), reads the MFT in 4 MB chunks via raw volume
// reads, parses each FILE record's attributes, then assembles a FolderNode tree
// from parent-FRN links. ~5–10x faster than the FindFirstFileEx walker because it
// reads metadata in one big sequential pass instead of per-folder syscalls.

use crate::scanner::{wide, ProgressFn};
use crate::types::{FileEntry, FolderNode, ScanProgress};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Instant;

use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, GetVolumeInformationW, ReadFile, SetFilePointerEx, FILE_BEGIN,
    FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::Ioctl::{FSCTL_GET_NTFS_VOLUME_DATA, NTFS_VOLUME_DATA_BUFFER};
use windows::Win32::System::IO::DeviceIoControl;

const GENERIC_READ: u32 = 0x80000000;

// ----------------------------------------------------------------------------
// Public API
// ----------------------------------------------------------------------------

pub struct MftScanner {
    cancel: Arc<AtomicBool>,
    progress: Option<Arc<ProgressFn>>,
    track_files: bool,

    files_scanned: AtomicI64,
    total_size: AtomicI64,
    last_report_ms: AtomicI64,
    mft_bytes_total: AtomicI64,
    mft_bytes_read: AtomicI64,
    start: Instant,
}

impl MftScanner {
    pub fn new() -> Self {
        Self {
            cancel: Arc::new(AtomicBool::new(false)),
            progress: None,
            track_files: false,
            files_scanned: AtomicI64::new(0),
            total_size: AtomicI64::new(0),
            last_report_ms: AtomicI64::new(0),
            mft_bytes_total: AtomicI64::new(0),
            mft_bytes_read: AtomicI64::new(0),
            start: Instant::now(),
        }
    }

    pub fn with_cancel(mut self, c: Arc<AtomicBool>) -> Self {
        self.cancel = c;
        self
    }

    pub fn with_progress(mut self, p: ProgressFn) -> Self {
        self.progress = Some(Arc::new(p));
        self
    }

    pub fn with_track_files(mut self, b: bool) -> Self {
        self.track_files = b;
        self
    }

    pub fn scan(&self, root: &str) -> Result<FolderNode, String> {
        let norm = root.trim_end_matches('\\').trim();
        if norm.len() < 2 || norm.as_bytes()[1] != b':' {
            return Err("MFT scan requires a drive-letter root (e.g. C:)".into());
        }
        let drive = (norm.as_bytes()[0] as char).to_ascii_uppercase();
        let vol_path = format!("\\\\.\\{drive}:");

        let vol_w = wide(&vol_path);
        let h = unsafe {
            CreateFileW(
                PCWSTR(vol_w.as_ptr()),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                None,
                OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES(0),
                None,
            )
        }
        .map_err(|e| format!("Cannot open {vol_path} (run as Administrator): {e}"))?;

        if h.is_invalid() || h == INVALID_HANDLE_VALUE {
            return Err(format!("Cannot open {vol_path} (run as Administrator)"));
        }
        let _guard = HandleGuard(h);

        // 1. NTFS volume parameters
        let vd = unsafe { get_volume_data(h) }?;
        let record_size = vd.BytesPerFileRecordSegment as usize;
        let sector_size = vd.BytesPerSector as usize;
        let bytes_per_cluster = vd.BytesPerCluster as i64;

        // 2. Read MFT record 0 → parse its $DATA data runs (where the MFT itself lives)
        let mut rec0 = vec![0u8; record_size];
        unsafe { read_at(h, vd.MftStartLcn * bytes_per_cluster, &mut rec0)? };
        apply_fixups(&mut rec0, 0, record_size, sector_size);
        let runs = extract_mft_data_runs(&rec0);
        if runs.is_empty() {
            return Err("Could not parse MFT data runs from record 0.".into());
        }

        // 3. Bulk-read the MFT, processing each record into an MftEntry
        let est_count = (vd.MftValidDataLength as i64 / record_size as i64).max(100_000);
        let cap = est_count.min(4_000_000) as usize;
        let mut entries: HashMap<i64, MftEntry> = HashMap::with_capacity(cap);
        let mut frn_cursor: i64 = 0;
        let mut total_bytes_remaining = vd.MftValidDataLength as i64;
        self.mft_bytes_total
            .store(vd.MftValidDataLength as i64, Ordering::SeqCst);
        self.mft_bytes_read.store(0, Ordering::SeqCst);
        const CHUNK: usize = 4 * 1024 * 1024;
        let mut buf = vec![0u8; CHUNK];

        for run in &runs {
            if self.cancel.load(Ordering::Relaxed) {
                return Err("cancelled".into());
            }
            if total_bytes_remaining <= 0 {
                break;
            }
            let mut pos = run.lcn * bytes_per_cluster;
            let mut run_bytes = run.length * bytes_per_cluster;
            while run_bytes > 0 && total_bytes_remaining > 0 {
                if self.cancel.load(Ordering::Relaxed) {
                    return Err("cancelled".into());
                }
                let want = (CHUNK as i64).min(run_bytes).min(total_bytes_remaining);
                let to_read = (want - (want % record_size as i64)) as usize;
                if to_read == 0 {
                    break;
                }

                unsafe { set_pos(h, pos)? };
                let mut bytes_read: u32 = 0;
                unsafe {
                    ReadFile(
                        h,
                        Some(&mut buf[..to_read]),
                        Some(&mut bytes_read),
                        None,
                    )
                }
                .map_err(|e| format!("ReadFile MFT failed: {e}"))?;
                if bytes_read == 0 {
                    break;
                }
                let process_bytes = (bytes_read as usize) - ((bytes_read as usize) % record_size);

                let mut off = 0;
                while off < process_bytes {
                    apply_fixups(&mut buf, off, record_size, sector_size);
                    self.process_record(&buf, off, record_size, frn_cursor, &mut entries);
                    frn_cursor += 1;
                    off += record_size;
                }
                pos += bytes_read as i64;
                run_bytes -= bytes_read as i64;
                total_bytes_remaining -= bytes_read as i64;
                self.mft_bytes_read
                    .fetch_add(bytes_read as i64, Ordering::Relaxed);
                self.report_progress();
            }
        }

        // 4. Wire FRN children, build FolderNode tree from root FRN=5
        self.build_tree(entries, drive)
    }

    fn process_record(
        &self,
        buf: &[u8],
        off: usize,
        rec_size: usize,
        frn: i64,
        entries: &mut HashMap<i64, MftEntry>,
    ) {
        if off + rec_size > buf.len() {
            return;
        }
        // "FILE" magic
        if &buf[off..off + 4] != b"FILE" {
            return;
        }

        let flags = u16_le(buf, off + 22);
        let in_use = (flags & 0x01) != 0;
        let is_dir = (flags & 0x02) != 0;
        if !in_use {
            return;
        }

        let first_attr_off = u16_le(buf, off + 20) as usize;
        let mut p = off + first_attr_off;
        let rec_end = off + rec_size;

        let mut best_name: Option<String> = None;
        let mut best_ns: u8 = 0xFF;
        let mut parent_frn: i64 = -1;
        let mut size: i64 = 0;
        let mut size_found = false;
        let mut last_write_ft: i64 = 0;

        while p + 16 < rec_end {
            let attr_type = u32_le(buf, p);
            if attr_type == 0xFFFFFFFF {
                break;
            }
            let alen = u32_le(buf, p + 4) as usize;
            if alen == 0 || p + alen > rec_end {
                break;
            }
            let non_resident = buf[p + 8];
            let attr_name_len = buf[p + 9];

            if attr_type == 0x10 && non_resident == 0 {
                // $STANDARD_INFORMATION (resident) — modification time at value+8
                let v_off = u16_le(buf, p + 20) as usize;
                let v = p + v_off;
                if v + 16 <= rec_end && last_write_ft == 0 {
                    last_write_ft = i64_le(buf, v + 8);
                }
            } else if attr_type == 0x30 && non_resident == 0 {
                // $FILE_NAME (resident) — prefer Win32 / Win32&DOS namespace
                let v_off = u16_le(buf, p + 20) as usize;
                let v = p + v_off;
                if v + 66 <= rec_end {
                    let parent_raw = i64_le(buf, v);
                    let pfrn = parent_raw & 0x0000_FFFF_FFFF_FFFF;
                    let mod_ft = i64_le(buf, v + 16);
                    let _real_size = i64_le(buf, v + 48);
                    let name_len = buf[v + 64] as usize;
                    let ns = buf[v + 65];
                    let name_byte_len = name_len * 2;
                    if v + 66 + name_byte_len <= rec_end {
                        let name = utf16le_to_string(&buf[v + 66..v + 66 + name_byte_len]);
                        let prio = name_priority(ns);
                        let cur_prio = if best_name.is_some() {
                            name_priority(best_ns)
                        } else {
                            -1
                        };
                        if prio > cur_prio {
                            best_name = Some(name);
                            best_ns = ns;
                            parent_frn = pfrn;
                            if last_write_ft == 0 {
                                last_write_ft = mod_ft;
                            }
                        }
                    }
                }
            } else if attr_type == 0x80 && attr_name_len == 0 && !size_found {
                // $DATA, default unnamed stream — first occurrence wins
                if non_resident == 0 {
                    size = u32_le(buf, p + 16) as i64;
                } else if p + 56 <= rec_end {
                    size = i64_le(buf, p + 48);
                }
                size_found = true;
            }

            p += alen;
        }

        let name = match best_name {
            Some(n) => n,
            None => return,
        };
        if is_dir {
            size = 0;
        }

        if !is_dir {
            self.files_scanned.fetch_add(1, Ordering::Relaxed);
            self.total_size.fetch_add(size, Ordering::Relaxed);
        }

        entries.insert(
            frn,
            MftEntry {
                parent_frn,
                name,
                size,
                is_dir,
                last_write_ft,
            },
        );
    }

    fn build_tree(
        &self,
        entries: HashMap<i64, MftEntry>,
        drive: char,
    ) -> Result<FolderNode, String> {
        if !entries.contains_key(&5) {
            return Err("MFT root entry (FRN 5) not found.".into());
        }

        // child FRNs per parent
        let mut kids: HashMap<i64, Vec<i64>> = HashMap::with_capacity(entries.len() / 4);
        for (&frn, e) in &entries {
            if frn == 5 {
                continue;
            }
            if entries.contains_key(&e.parent_frn) {
                kids.entry(e.parent_frn).or_default().push(frn);
            }
        }

        let root_path = format!("{drive}:\\");
        let mut root_node = FolderNode::default();
        root_node.full_path = root_path.clone();
        root_node.name = root_path.clone();
        root_node.last_modified_ft = entries[&5].last_write_ft;

        build_subtree(5, &mut root_node, &root_path, &entries, &kids, self.track_files);
        Ok(root_node)
    }

    fn report_progress(&self) {
        let progress = match &self.progress {
            Some(p) => p,
            None => return,
        };
        let now_ms = self.start.elapsed().as_millis() as i64;
        let last = self.last_report_ms.load(Ordering::Relaxed);
        if now_ms - last < 80 {
            return;
        }
        if self
            .last_report_ms
            .compare_exchange(last, now_ms, Ordering::SeqCst, Ordering::Relaxed)
            .is_err()
        {
            return;
        }
        let total = self.total_size.load(Ordering::Relaxed);
        let files = self.files_scanned.load(Ordering::Relaxed);
        let mft_total = self.mft_bytes_total.load(Ordering::Relaxed);
        // Reserve the last 5% for tree-build; cap read-progress at 95%.
        let percent = if mft_total > 0 {
            let pct = 95.0 * (self.mft_bytes_read.load(Ordering::Relaxed) as f64) / (mft_total as f64);
            pct.clamp(0.0, 95.0)
        } else {
            -1.0
        };
        progress(&ScanProgress {
            total_size: total,
            files_scanned: files,
            current_path: "MFT scan in progress...".to_string(),
            percent,
        });
    }
}

// ----------------------------------------------------------------------------
// is_ntfs_drive_root — caller checks this before invoking MftScanner.
// ----------------------------------------------------------------------------

#[allow(dead_code)] // called by the GUI layer; main.rs uses --mft for now
pub fn is_ntfs_drive_root(path: &str) -> bool {
    if path.is_empty() {
        return false;
    }
    let mut trimmed = path.trim().to_string();
    if trimmed.len() == 2 && trimmed.as_bytes()[1] == b':' {
        trimmed.push('\\');
    }
    if trimmed.len() != 3 || trimmed.as_bytes()[1] != b':' || trimmed.as_bytes()[2] != b'\\' {
        return false;
    }
    let root_w = wide(&trimmed);
    let mut fs_buf = [0u16; 20];
    let mut label_buf = [0u16; 64];
    let mut serial: u32 = 0;
    let mut max_len: u32 = 0;
    let mut flags: u32 = 0;
    let ok = unsafe {
        GetVolumeInformationW(
            PCWSTR(root_w.as_ptr()),
            Some(&mut label_buf),
            Some(&mut serial),
            Some(&mut max_len),
            Some(&mut flags),
            Some(&mut fs_buf),
        )
    };
    if ok.is_err() {
        return false;
    }
    let fs = crate::scanner::wstr_to_string(&fs_buf);
    fs.eq_ignore_ascii_case("NTFS")
}

// ----------------------------------------------------------------------------
// Internal data structures
// ----------------------------------------------------------------------------

struct MftEntry {
    parent_frn: i64,
    name: String,
    size: i64,
    is_dir: bool,
    last_write_ft: i64,
}

#[derive(Copy, Clone)]
struct DataRun {
    lcn: i64,
    length: i64,
}

struct HandleGuard(HANDLE);
impl Drop for HandleGuard {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

// ----------------------------------------------------------------------------
// Volume / I/O helpers
// ----------------------------------------------------------------------------

unsafe fn get_volume_data(h: HANDLE) -> Result<NTFS_VOLUME_DATA_BUFFER, String> {
    let mut out: NTFS_VOLUME_DATA_BUFFER = std::mem::zeroed();
    let sz = std::mem::size_of::<NTFS_VOLUME_DATA_BUFFER>() as u32;
    let mut returned: u32 = 0;
    DeviceIoControl(
        h,
        FSCTL_GET_NTFS_VOLUME_DATA,
        None,
        0,
        Some(&mut out as *mut _ as *mut _),
        sz,
        Some(&mut returned),
        None,
    )
    .map_err(|e| format!("FSCTL_GET_NTFS_VOLUME_DATA failed: {e}"))?;
    Ok(out)
}

unsafe fn set_pos(h: HANDLE, pos: i64) -> Result<(), String> {
    let mut np: i64 = 0;
    SetFilePointerEx(h, pos, Some(&mut np), FILE_BEGIN)
        .map_err(|e| format!("SetFilePointerEx({pos}) failed: {e}"))
}

unsafe fn read_at(h: HANDLE, pos: i64, buf: &mut [u8]) -> Result<(), String> {
    set_pos(h, pos)?;
    let mut got: u32 = 0;
    ReadFile(h, Some(buf), Some(&mut got), None)
        .map_err(|e| format!("ReadFile at {pos} failed: {e}"))
}

// ----------------------------------------------------------------------------
// USA fixups (NTFS multi-sector transfer protection)
// ----------------------------------------------------------------------------

fn apply_fixups(buf: &mut [u8], rec_off: usize, rec_size: usize, sector_size: usize) {
    if buf.len() < rec_off + 8 {
        return;
    }
    if &buf[rec_off..rec_off + 4] != b"FILE" {
        return;
    }
    let usa_offset = u16_le(buf, rec_off + 4) as usize;
    let usa_count = u16_le(buf, rec_off + 6) as usize;
    if usa_count < 1 {
        return;
    }
    let usa_pos = rec_off + usa_offset;
    for i in 1..usa_count {
        let sector_end = rec_off + i * sector_size - 2;
        if sector_end + 2 > rec_off + rec_size {
            break;
        }
        let src = usa_pos + i * 2;
        if src + 2 > buf.len() {
            break;
        }
        buf[sector_end] = buf[src];
        buf[sector_end + 1] = buf[src + 1];
    }
}

// ----------------------------------------------------------------------------
// Find the MFT's own $DATA data runs (record 0)
// ----------------------------------------------------------------------------

fn extract_mft_data_runs(rec: &[u8]) -> Vec<DataRun> {
    let first_attr = u16_le(rec, 20) as usize;
    let mut p = first_attr;
    while p + 8 < rec.len() {
        let attr_type = u32_le(rec, p);
        if attr_type == 0xFFFFFFFF {
            break;
        }
        let alen = u32_le(rec, p + 4) as usize;
        if alen == 0 || p + alen > rec.len() {
            break;
        }
        // Non-resident (buf[p+8]==1) unnamed (buf[p+9]==0) $DATA (type 0x80)
        if attr_type == 0x80 && rec[p + 8] == 1 && rec[p + 9] == 0 {
            let run_offset = u16_le(rec, p + 32) as usize;
            return parse_data_runs(rec, p + run_offset, p + alen);
        }
        p += alen;
    }
    Vec::new()
}

fn parse_data_runs(buf: &[u8], start: usize, end: usize) -> Vec<DataRun> {
    let mut runs = Vec::new();
    let mut prev_lcn: i64 = 0;
    let mut p = start;
    while p < end {
        let header = buf[p];
        p += 1;
        if header == 0 {
            break;
        }
        let len_bytes = (header & 0x0F) as usize;
        let off_bytes = ((header >> 4) & 0x0F) as usize;
        if len_bytes == 0 {
            break;
        }
        if p + len_bytes > end {
            break;
        }
        let length = read_signed_le(buf, p, len_bytes);
        p += len_bytes;
        if off_bytes == 0 {
            // sparse run — skip
            continue;
        }
        if p + off_bytes > end {
            break;
        }
        let offset = read_signed_le(buf, p, off_bytes);
        p += off_bytes;
        let lcn = prev_lcn + offset;
        prev_lcn = lcn;
        runs.push(DataRun { lcn, length });
    }
    runs
}

fn read_signed_le(buf: &[u8], off: usize, len: usize) -> i64 {
    let mut v: i64 = 0;
    for i in 0..len {
        v |= (buf[off + i] as i64) << (i * 8);
    }
    if len < 8 && (buf[off + len - 1] & 0x80) != 0 {
        v |= !((1i64 << (len * 8)) - 1);
    }
    v
}

// ----------------------------------------------------------------------------
// Tree assembly
// ----------------------------------------------------------------------------

fn build_subtree(
    frn: i64,
    node: &mut FolderNode,
    node_path: &str,
    entries: &HashMap<i64, MftEntry>,
    kids: &HashMap<i64, Vec<i64>>,
    track_files: bool,
) {
    let kid_frns = match kids.get(&frn) {
        Some(v) => v,
        None => return,
    };
    for &child_frn in kid_frns {
        let c = match entries.get(&child_frn) {
            Some(e) => e,
            None => continue,
        };
        if c.is_dir {
            let child_path = if node_path.ends_with('\\') {
                format!("{node_path}{}", c.name)
            } else {
                format!("{node_path}\\{}", c.name)
            };
            let mut child = FolderNode::default();
            child.full_path = child_path.clone();
            child.name = c.name.clone();
            child.last_modified_ft = c.last_write_ft;
            build_subtree(child_frn, &mut child, &child_path, entries, kids, track_files);
            node.size += child.size;
            node.file_count += child.file_count;
            node.folder_count += child.folder_count + 1;
            node.children.push(child);
        } else {
            node.own_size += c.size;
            node.size += c.size;
            node.direct_file_count += 1;
            node.file_count += 1;
            if track_files {
                node.files.push(FileEntry {
                    name: c.name.clone(),
                    size: c.size,
                    last_modified_ft: c.last_write_ft,
                });
            }
        }
    }
}

fn name_priority(ns: u8) -> i32 {
    // Win32&DOS combined > Win32 > POSIX > DOS
    match ns {
        3 => 4,
        1 => 3,
        0 => 2,
        2 => 1,
        _ => 0,
    }
}

// ----------------------------------------------------------------------------
// Byte helpers
// ----------------------------------------------------------------------------

#[inline]
fn u16_le(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes([b[o], b[o + 1]])
}

#[inline]
fn u32_le(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

#[inline]
fn i64_le(b: &[u8], o: usize) -> i64 {
    i64::from_le_bytes([
        b[o], b[o + 1], b[o + 2], b[o + 3], b[o + 4], b[o + 5], b[o + 6], b[o + 7],
    ])
}

fn utf16le_to_string(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&units)
}
