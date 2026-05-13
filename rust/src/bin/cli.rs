// Console driver — invokes the scanner modules from the lib crate. Useful for
// validating the scanners independently of the GUI.

use cluttercutter::{mft, scanner, types};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Instant;

fn main() {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    let mut use_mft = false;
    args.retain(|a| {
        if a == "--mft" {
            use_mft = true;
            false
        } else {
            true
        }
    });
    let root = match args.into_iter().next() {
        Some(p) => p,
        None => {
            eprintln!("usage: cluttercutter-cli.exe [--mft] <path>");
            std::process::exit(2);
        }
    };

    let cancel = Arc::new(AtomicBool::new(false));
    let progress: scanner::ProgressFn = Box::new(|p| {
        eprint!(
            "\r  {:>8} files  {:>10}  {:>5}  {}",
            p.files_scanned,
            fmt_bytes(p.total_size),
            if p.percent < 0.0 {
                "  ?  ".to_string()
            } else {
                format!("{:>4.1}%", p.percent)
            },
            truncate(&p.current_path, 70),
        );
    });

    let start = Instant::now();
    let result = if use_mft {
        mft::MftScanner::new()
            .with_cancel(cancel.clone())
            .with_progress(progress)
            .scan(&root)
    } else {
        scanner::Scanner::new()
            .with_cancel(cancel.clone())
            .with_progress(progress)
            .scan(&root)
            .map_err(|s| s.to_string())
    };
    let elapsed = start.elapsed();
    eprintln!();

    let root = match result {
        Ok(r) => r,
        Err(e) => {
            eprintln!("Scan failed: {e}");
            std::process::exit(1);
        }
    };

    println!(
        "\n{} — {} ({} files, {} folders) in {:.2}s [{}]",
        root.name,
        fmt_bytes(root.size),
        root.file_count,
        root.folder_count,
        elapsed.as_secs_f64(),
        if use_mft { "MFT" } else { "walker" },
    );

    let mut kids: Vec<&types::FolderNode> = root.children.iter().collect();
    kids.sort_by(|a, b| b.size.cmp(&a.size));
    for k in kids.iter().take(20) {
        println!("  {:>10}  {}", fmt_bytes(k.size), k.name);
    }
}

fn fmt_bytes(n: i64) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut v = n as f64;
    let mut u = 0;
    while v >= 1024.0 && u < UNITS.len() - 1 {
        v /= 1024.0;
        u += 1;
    }
    format!("{v:.1} {}", UNITS[u])
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    let trimmed: String = s.chars().take(max - 1).collect();
    format!("{trimmed}…")
}
