//! Platform-independent text helpers of the prototype window (unit-tested on any host).

use sshx_core::sorting::Entry;

pub fn cell_text(e: &Entry, column: i32) -> String {
    match column {
        0 => e.name.clone(),
        1 => match e.size {
            Some(s) if s >= 1 << 30 => format!("{:.1} ГБ", s as f64 / (1u64 << 30) as f64),
            Some(s) if s >= 1 << 20 => format!("{:.1} МБ", s as f64 / (1u64 << 20) as f64),
            Some(s) if s >= 1024 => format!("{:.1} КБ", s as f64 / 1024.0),
            Some(s) => format!("{s} Б"),
            None => if e.is_dir { String::new() } else { "—".into() },
        },
        2 => e.modified.map(format_time).unwrap_or_else(|| "—".into()),
        3 => e.permissions.map(|p| format!("{p:04o}")).unwrap_or_else(|| "—".into()),
        _ => String::new(),
    }
}

pub fn format_time(secs: u64) -> String {
    // UTC calendar from Unix seconds (civil-from-days); no time-zone database needed.
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    format!("{:04}-{:02}-{:02} {:02}:{:02} UTC", if m <= 2 { y + 1 } else { y }, m, d, rem / 3600, rem % 3600 / 60)
}

pub fn icon_key(e: &Entry) -> String {
    if e.is_dir { return "\0dir".into(); }
    match e.name.rsplit_once('.') { Some((stem, ext)) if !stem.is_empty() && !ext.is_empty() && ext.len() <= 16 => format!(".{}", ext.to_lowercase()), _ => String::new() }
}

pub fn json_str(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c { '"' => out.push_str("\\\""), '\\' => out.push_str("\\\\"), c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)), c => out.push(c) }
    }
    out.push('"');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn utc_calendar() {
        assert_eq!(format_time(0), "1970-01-01 00:00 UTC");
        assert_eq!(format_time(1_788_000_000), "2026-08-29 10:40 UTC");
        assert_eq!(format_time(951_782_400), "2000-02-29 00:00 UTC");
    }

    #[test]
    fn sizes_and_unknown_values() {
        let e = Entry { name: "a".into(), is_dir: false, size: Some(1536), modified: None, permissions: None };
        assert_eq!(cell_text(&e, 1), "1.5 КБ");
        assert_eq!(cell_text(&e, 2), "—");
        let d = Entry { name: "d".into(), is_dir: true, size: None, modified: None, permissions: Some(0o755) };
        assert_eq!(cell_text(&d, 1), "");
        assert_eq!(cell_text(&d, 3), "0755");
    }

    #[test]
    fn icon_keys_by_extension_only() {
        let f = |n: &str| icon_key(&Entry { name: n.into(), is_dir: false, size: None, modified: None, permissions: None });
        assert_eq!(f("Report.PDF"), ".pdf");
        assert_eq!(f(".bashrc"), "");
        assert_eq!(f("archive.tar.gz"), ".gz");
        assert_eq!(f("noext"), "");
    }

    #[test]
    fn json_escaping() { assert_eq!(json_str("a\"b\\c\n"), "\"a\\\"b\\\\c\\u000a\""); }
}
