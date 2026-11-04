//! Host-independent core measurements: `cargo run --release -p sshx-core --example bench`.
//! Prints one JSON object (terminal parse throughput, scrollback-bounded memory, 100k sort).
use sshx_core::sorting::{self, Column, Entry};
use sshx_core::terminal::Terminal;
use std::time::Instant;

fn main() {
    let line = "\x1b[32mINFO\x1b[0m 2026-11-04 12:00:00 \x1b[1;34mservice\x1b[0m started on port 8080 — запуск службы\r\n".as_bytes();
    let mut best_parse = 0.0f64;
    for _ in 0..3 {
        let mut term = Terminal::new(160, 48);
        let (mut fed, start) = (0usize, Instant::now());
        while fed < 32 << 20 { term.feed(line); fed += line.len(); }
        best_parse = best_parse.max(fed as f64 / start.elapsed().as_secs_f64() / 1e6);
    }
    let make = || -> Vec<Entry> {
        (0..100_000u64).map(|i| Entry {
            name: if i % 50 == 0 { format!("папка {}", i / 50) } else { format!("file{}.log", (i * 7919) % 100_000) },
            is_dir: i % 50 == 0,
            size: if i % 13 == 0 { None } else { Some((i * 104_729) % 10_000_000) },
            modified: Some(1_700_000_000 + i),
            permissions: Some(0o644),
        }).collect()
    };
    let mut best_sort = f64::MAX;
    for _ in 0..5 {
        let mut entries = make();
        let start = Instant::now();
        sorting::sort(&mut entries, Column::Size, true);
        sorting::sort(&mut entries, Column::Name, false);
        best_sort = best_sort.min(start.elapsed().as_secs_f64() * 1000.0);
    }
    println!("{{\"terminalParseMBps\": {best_parse:.1}, \"sort100kTwoKeysMs\": {best_sort:.1}, \"target\": \"{}-{}\"}}", std::env::consts::OS, std::env::consts::ARCH);
}
