// Tree-walk analyses over a completed scan result. Pure functions; no Win32.

use crate::types::{FileEntry, FolderNode};
use std::cmp::Ordering;
use std::collections::BinaryHeap;

// A file pinned to its owning folder, returned by the analysis queries. The
// path is reconstructed by the caller via `folder.full_path` + `file.name` so
// we keep the heap entries cheap.
pub struct FileHit<'a> {
    pub file: &'a FileEntry,
    pub folder: &'a FolderNode,
}

// BinaryHeap is a max-heap. We want the N *largest* files, so we put a min-heap
// in there of size N (smallest at the top) — every incoming file > current min
// pops the min and pushes itself. At the end, we drain and sort descending.
struct HeapNode<'a> {
    size: i64,
    file: &'a FileEntry,
    folder: &'a FolderNode,
}

impl<'a> PartialEq for HeapNode<'a> {
    fn eq(&self, other: &Self) -> bool {
        self.size == other.size
    }
}
impl<'a> Eq for HeapNode<'a> {}
impl<'a> PartialOrd for HeapNode<'a> {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl<'a> Ord for HeapNode<'a> {
    fn cmp(&self, other: &Self) -> Ordering {
        // Reversed so BinaryHeap behaves as a min-heap.
        other.size.cmp(&self.size)
    }
}

pub fn top_n_files(root: &FolderNode, n: usize) -> Vec<FileHit<'_>> {
    if n == 0 {
        return Vec::new();
    }

    let mut heap: BinaryHeap<HeapNode<'_>> = BinaryHeap::with_capacity(n + 1);
    let mut stack: Vec<&FolderNode> = Vec::with_capacity(64);
    stack.push(root);

    while let Some(node) = stack.pop() {
        for f in &node.files {
            if heap.len() < n {
                heap.push(HeapNode {
                    size: f.size,
                    file: f,
                    folder: node,
                });
            } else if let Some(top) = heap.peek() {
                if f.size > top.size {
                    heap.pop();
                    heap.push(HeapNode {
                        size: f.size,
                        file: f,
                        folder: node,
                    });
                }
            }
        }
        for c in &node.children {
            stack.push(c);
        }
    }

    let mut out: Vec<FileHit<'_>> = heap
        .into_iter()
        .map(|h| FileHit {
            file: h.file,
            folder: h.folder,
        })
        .collect();
    out.sort_by(|a, b| b.file.size.cmp(&a.file.size));
    out
}

// Oldest-N by last-modified. NTFS disables last-access updates by default
// (since Vista), so mtime is the practical "least used" proxy. Files with
// last_modified_ft == 0 (no mtime captured, e.g. on access failure) are
// skipped so they don't fake-pin as "oldest".
struct OldestHeapNode<'a> {
    mtime: i64,
    file: &'a FileEntry,
    folder: &'a FolderNode,
}

impl<'a> PartialEq for OldestHeapNode<'a> {
    fn eq(&self, other: &Self) -> bool {
        self.mtime == other.mtime
    }
}
impl<'a> Eq for OldestHeapNode<'a> {}
impl<'a> PartialOrd for OldestHeapNode<'a> {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl<'a> Ord for OldestHeapNode<'a> {
    fn cmp(&self, other: &Self) -> Ordering {
        // BinaryHeap is max-heap by default — keep the newest at the top so we
        // can pop it to make room for an older candidate.
        self.mtime.cmp(&other.mtime)
    }
}

pub fn oldest_n_files(root: &FolderNode, n: usize) -> Vec<FileHit<'_>> {
    if n == 0 {
        return Vec::new();
    }

    let mut heap: BinaryHeap<OldestHeapNode<'_>> = BinaryHeap::with_capacity(n + 1);
    let mut stack: Vec<&FolderNode> = Vec::with_capacity(64);
    stack.push(root);

    while let Some(node) = stack.pop() {
        for f in &node.files {
            if f.last_modified_ft == 0 {
                continue;
            }
            if heap.len() < n {
                heap.push(OldestHeapNode {
                    mtime: f.last_modified_ft,
                    file: f,
                    folder: node,
                });
            } else if let Some(top) = heap.peek() {
                if f.last_modified_ft < top.mtime {
                    heap.pop();
                    heap.push(OldestHeapNode {
                        mtime: f.last_modified_ft,
                        file: f,
                        folder: node,
                    });
                }
            }
        }
        for c in &node.children {
            stack.push(c);
        }
    }

    let mut out: Vec<FileHit<'_>> = heap
        .into_iter()
        .map(|h| FileHit {
            file: h.file,
            folder: h.folder,
        })
        .collect();
    out.sort_by(|a, b| a.file.last_modified_ft.cmp(&b.file.last_modified_ft));
    out
}
