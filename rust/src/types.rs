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
