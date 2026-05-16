// Mirror of the FolderNode / ScanProgress shapes in ClutterCutter.cs.
// `parent` pointer is intentionally omitted — the GUI tracks the current path
// stack separately, avoiding the self-referential tree.

#[allow(dead_code)] // last_modified_ft + percent are consumed by the GUI layer (next stage)
#[derive(Default)]
pub struct FolderNode {
    pub full_path: String,
    pub name: String,
    pub size: i64,
    pub own_size: i64,
    pub file_count: i64,
    pub folder_count: i64,
    pub direct_file_count: i64,
    pub children: Vec<FolderNode>,
    // Per-file entries directly owned by this folder. Empty unless the scanner
    // was configured with `track_files(true)`.
    pub files: Vec<FileEntry>,
    pub is_access_denied: bool,
    pub last_modified_ft: i64,
}

#[allow(dead_code)]
#[derive(Default, Clone)]
pub struct ScanProgress {
    pub total_size: i64,
    pub files_scanned: i64,
    pub current_path: String,
    pub percent: f64, // -1.0 = indeterminate
}

// Individual file entry retained when the scanner runs with track_files enabled.
// `name` is the leaf name; the full path is reconstructed from the owning
// FolderNode's full_path during display, which keeps memory small for deep trees.
#[derive(Clone)]
pub struct FileEntry {
    pub name: String,
    pub size: i64,
    pub last_modified_ft: i64,
}
