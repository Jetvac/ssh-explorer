//! Typed table sorting shared with the WPF rules: natural names, folders first, unknown last in
//! both directions, a stable secondary key (name A→Z) for equal values.

use std::cmp::Ordering;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Entry {
    pub name: String,
    pub is_dir: bool,
    pub size: Option<u64>,
    pub modified: Option<u64>,
    pub permissions: Option<u32>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Column { Name, Size, Modified, Permissions }

/// Natural order: "file2" < "file10", case-insensitive first, ordinal as the final tie-breaker.
/// Allocation-free: digit runs are compared as byte slices (ASCII digits), other characters one
/// at a time after simple lowercase folding.
pub fn natural_cmp(a: &str, b: &str) -> Ordering {
    let (ab, bb) = (a.as_bytes(), b.as_bytes());
    let (mut i, mut j) = (0, 0);
    while i < ab.len() && j < bb.len() {
        if ab[i].is_ascii_digit() && bb[j].is_ascii_digit() {
            let (si, sj) = (i, j);
            while i < ab.len() && ab[i].is_ascii_digit() { i += 1; }
            while j < bb.len() && bb[j].is_ascii_digit() { j += 1; }
            let (na, nb) = (trim_zeros(&ab[si..i]), trim_zeros(&bb[sj..j]));
            let ord = na.len().cmp(&nb.len()).then_with(|| na.cmp(nb));
            if ord != Ordering::Equal { return ord; }
        } else {
            let (ca, cb) = (a[i..].chars().next().unwrap_or('\0'), b[j..].chars().next().unwrap_or('\0'));
            let (la, lb) = (fold(ca), fold(cb));
            if la != lb { return la.cmp(&lb); }
            i += ca.len_utf8();
            j += cb.len_utf8();
        }
    }
    (ab.len() - i).cmp(&(bb.len() - j)).then_with(|| a.cmp(b))
}

fn trim_zeros(digits: &[u8]) -> &[u8] {
    let first = digits.iter().position(|&d| d != b'0').unwrap_or(digits.len());
    &digits[first..]
}

fn fold(c: char) -> char { if c.is_ascii() { c.to_ascii_lowercase() } else { c.to_lowercase().next().unwrap_or(c) } }

fn known_last<T: Ord>(a: Option<T>, b: Option<T>, descending: bool) -> Ordering {
    match (a, b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Greater,
        (Some(_), None) => Ordering::Less,
        (Some(x), Some(y)) => if descending { y.cmp(&x) } else { x.cmp(&y) },
    }
}

pub fn compare(a: &Entry, b: &Entry, column: Column, descending: bool) -> Ordering {
    if a.is_dir != b.is_dir { return if a.is_dir { Ordering::Less } else { Ordering::Greater }; }
    let primary = match column {
        Column::Name => { let o = natural_cmp(&a.name, &b.name); if descending { o.reverse() } else { o } }
        Column::Size => known_last(a.size, b.size, descending),
        Column::Modified => known_last(a.modified, b.modified, descending),
        Column::Permissions => known_last(a.permissions, b.permissions, descending),
    };
    primary.then_with(|| natural_cmp(&a.name, &b.name))
}

pub fn sort(entries: &mut [Entry], column: Column, descending: bool) {
    entries.sort_by(|a, b| compare(a, b, column, descending));
}

#[cfg(test)]
mod tests {
    use super::*;
    fn file(name: &str, size: Option<u64>) -> Entry { Entry { name: name.into(), is_dir: false, size, modified: None, permissions: None } }

    #[test]
    fn natural_names_and_folders_first() {
        let mut v = vec![file("file10", Some(1)), file("file2", Some(1)), Entry { name: "zdir".into(), is_dir: true, size: None, modified: None, permissions: None }, file("File1", Some(1))];
        sort(&mut v, Column::Name, false);
        assert_eq!(v.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(), ["zdir", "File1", "file2", "file10"]);
    }

    #[test]
    fn sizes_by_bytes_unknown_last_both_directions() {
        let mut v = vec![file("a", Some(1024 * 1024)), file("b", None), file("c", Some(900 * 1024))];
        sort(&mut v, Column::Size, false);
        assert_eq!(v.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(), ["c", "a", "b"]);
        sort(&mut v, Column::Size, true);
        assert_eq!(v.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(), ["a", "c", "b"]);
    }

    #[test]
    fn equal_values_use_stable_name_order() {
        let mut v = vec![file("b", Some(5)), file("a", Some(5))];
        sort(&mut v, Column::Size, true);
        assert_eq!(v[0].name, "a");
    }
}
