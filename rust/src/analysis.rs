// Tree-walk analyses over a completed scan result. Pure functions; no Win32.

use crate::types::{FileEntry, FolderNode};
use std::cmp::Ordering;
use std::collections::BinaryHeap;

// A file pinned to its owning folder, returned by `top_n_files`. The path is
// reconstructed by the caller via `folder.full_path` + `file.name` so we keep
// the heap entries cheap.
pub struct TopFileHit<'a> {
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

pub fn top_n_files(root: &FolderNode, n: usize) -> Vec<TopFileHit<'_>> {
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

    let mut out: Vec<TopFileHit<'_>> = heap
        .into_iter()
        .map(|h| TopFileHit {
            file: h.file,
            folder: h.folder,
        })
        .collect();
    out.sort_by(|a, b| b.file.size.cmp(&a.file.size));
    out
}
