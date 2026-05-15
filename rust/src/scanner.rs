// FindFirstFileExW-based recursive directory walker.
// Direct port of the `Scanner` class in ClutterCutter.cs — same FindExInfoBasic +
// LARGE_FETCH fast path, same parallel top-level fan-out, same progress throttling.

use crate::types::{FileEntry, FolderNode, ScanProgress};
use std::ffi::OsStr;
use std::iter::once;
use std::os::windows::ffi::OsStrExt;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Instant;

use windows::core::PCWSTR;
use windows::Win32::Foundation::{ERROR_ACCESS_DENIED, GetLastError, INVALID_HANDLE_VALUE};
use windows::Win32::Storage::FileSystem::{
    FindClose, FindFirstFileExW, FindNextFileW, FILE_ATTRIBUTE_DIRECTORY,
    FILE_ATTRIBUTE_REPARSE_POINT, FIND_FIRST_EX_LARGE_FETCH, FindExInfoBasic,
    FindExSearchNameMatch, WIN32_FIND_DATAW,
};

pub type ProgressFn = Box<dyn Fn(&ScanProgress) + Send + Sync>;

pub struct Scanner {
    cancel: Arc<AtomicBool>,
    progress: Option<Arc<ProgressFn>>,
    parallel_top_levels: i32,
    pub total_size_hint: i64,
    track_files: bool,

    total_size: AtomicI64,
    files_scanned: AtomicI64,
    last_report_ms: AtomicI64,
    start: Instant,
}

impl Scanner {
    pub fn new() -> Self {
        Self {
            cancel: Arc::new(AtomicBool::new(false)),
            progress: None,
            parallel_top_levels: 2,
            total_size_hint: 0,
            track_files: false,
            total_size: AtomicI64::new(0),
            files_scanned: AtomicI64::new(0),
            last_report_ms: AtomicI64::new(0),
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

    #[allow(dead_code)] // exposed for the eventual GUI to tune fan-out at runtime
    pub fn with_parallelism(mut self, depth: i32) -> Self {
        self.parallel_top_levels = depth;
        self
    }

    pub fn scan(&self, root: &str) -> Result<FolderNode, &'static str> {
        let mut path = root.trim().to_string();
        if path.len() > 3 && path.ends_with('\\') {
            path = path.trim_end_matches('\\').to_string();
        }
        let node = self.scan_folder(&path, true, self.parallel_top_levels);
        if self.cancel.load(Ordering::Relaxed) {
            return Err("cancelled");
        }
        Ok(node)
    }

    fn scan_folder(&self, path: &str, is_root: bool, parallel_depth: i32) -> FolderNode {
        if self.cancel.load(Ordering::Relaxed) {
            return FolderNode::default();
        }

        let mut node = FolderNode::default();
        node.full_path = path.to_string();
        node.name = if is_root {
            path.to_string()
        } else {
            std::path::Path::new(path)
                .file_name()
                .map(|s| s.to_string_lossy().into_owned())
                .unwrap_or_default()
        };

        let find_path = if path.ends_with('\\') {
            format!("{path}*")
        } else {
            format!("{path}\\*")
        };
        let find_path = if find_path.len() > 240 {
            to_long_path(&find_path)
        } else {
            find_path
        };
        let find_path_w = wide(&find_path);

        let mut fd: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
        let h = unsafe {
            FindFirstFileExW(
                PCWSTR(find_path_w.as_ptr()),
                FindExInfoBasic,
                &mut fd as *mut _ as *mut _,
                FindExSearchNameMatch,
                None,
                FIND_FIRST_EX_LARGE_FETCH,
            )
        };
        let h = match h {
            Ok(h) if !h.is_invalid() && h != INVALID_HANDLE_VALUE => h,
            _ => {
                let err = unsafe { GetLastError() };
                if err == ERROR_ACCESS_DENIED {
                    node.is_access_denied = true;
                }
                return node;
            }
        };

        let mut subdirs: Vec<String> = Vec::new();
        loop {
            let name = wstr_to_string(&fd.cFileName);
            if name != "." && name != ".." {
                let is_dir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY.0) != 0;
                let is_reparse = (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0) != 0;
                if is_dir {
                    if !is_reparse {
                        let child_path = if path.ends_with('\\') {
                            format!("{path}{name}")
                        } else {
                            format!("{path}\\{name}")
                        };
                        subdirs.push(child_path);
                    }
                } else {
                    let size = ((fd.nFileSizeHigh as i64) << 32) | (fd.nFileSizeLow as i64);
                    node.own_size += size;
                    node.size += size;
                    node.direct_file_count += 1;
                    node.file_count += 1;
                    self.files_scanned.fetch_add(1, Ordering::Relaxed);
                    self.total_size.fetch_add(size, Ordering::Relaxed);
                    if self.track_files {
                        let mtime = ((fd.ftLastWriteTime.dwHighDateTime as i64) << 32)
                            | (fd.ftLastWriteTime.dwLowDateTime as i64);
                        node.files.push(FileEntry {
                            name: name.clone(),
                            size,
                            last_modified_ft: mtime,
                        });
                    }
                }
            }
            let next = unsafe { FindNextFileW(h, &mut fd as *mut _ as *mut _) };
            if next.is_err() {
                break;
            }
        }
        unsafe {
            let _ = FindClose(h);
        }

        if self.cancel.load(Ordering::Relaxed) {
            return node;
        }

        if !subdirs.is_empty() {
            if parallel_depth > 0 && subdirs.len() > 1 {
                let child_parallel = parallel_depth - 1;
                let children: Vec<FolderNode> = std::thread::scope(|s| {
                    let handles: Vec<_> = subdirs
                        .iter()
                        .map(|p| {
                            let me = self;
                            let p = p.clone();
                            s.spawn(move || me.scan_folder(&p, false, child_parallel))
                        })
                        .collect();
                    handles
                        .into_iter()
                        .map(|h| h.join().unwrap_or_default())
                        .collect()
                });
                for c in children {
                    node.size += c.size;
                    node.file_count += c.file_count;
                    node.folder_count += c.folder_count + 1;
                    node.children.push(c);
                }
            } else {
                for p in &subdirs {
                    if self.cancel.load(Ordering::Relaxed) {
                        break;
                    }
                    let c = self.scan_folder(p, false, 0);
                    node.size += c.size;
                    node.file_count += c.file_count;
                    node.folder_count += c.folder_count + 1;
                    node.children.push(c);
                }
            }
        }

        self.report_progress(path);
        node
    }

    fn report_progress(&self, path: &str) {
        let progress = match &self.progress {
            Some(p) => p,
            None => return,
        };
        let now_ms = self.start.elapsed().as_millis() as i64;
        let last = self.last_report_ms.load(Ordering::Relaxed);
        // throttle to ~12 reports/sec across all threads
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
        let percent = if self.total_size_hint > 0 {
            let pct = 100.0 * (total as f64) / (self.total_size_hint as f64);
            pct.clamp(0.0, 99.5)
        } else {
            -1.0
        };
        progress(&ScanProgress {
            total_size: total,
            files_scanned: files,
            current_path: path.to_string(),
            percent,
        });
    }
}

pub(crate) fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(once(0)).collect()
}

pub(crate) fn wstr_to_string(buf: &[u16]) -> String {
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}

fn to_long_path(p: &str) -> String {
    if p.is_empty() {
        return p.to_string();
    }
    if p.starts_with(r"\\?\") {
        return p.to_string();
    }
    if p.starts_with(r"\\") {
        return format!(r"\\?\UNC\{}", &p[2..]);
    }
    format!(r"\\?\{p}")
}
