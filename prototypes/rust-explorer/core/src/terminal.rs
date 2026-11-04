//! VT/ANSI terminal model: vte parser + grid, scrollback, alternate screen, SGR colours,
//! wide characters, selection text and search. Rendering is done by the UI (GDI in the app).

use std::collections::VecDeque;
use unicode_width::UnicodeWidthChar;
use vte::{Params, Parser, Perform};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Color {
    Default,
    Indexed(u8),
    Rgb(u8, u8, u8),
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Attr {
    pub fg: Color,
    pub bg: Color,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    pub inverse: bool,
}

impl Default for Attr {
    fn default() -> Self {
        Attr { fg: Color::Default, bg: Color::Default, bold: false, italic: false, underline: false, inverse: false }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Cell {
    pub ch: char,
    pub attr: Attr,
    /// Right half of a double-width character; not drawn on its own.
    pub spacer: bool,
}

impl Default for Cell {
    fn default() -> Self {
        Cell { ch: ' ', attr: Attr::default(), spacer: false }
    }
}

pub type Row = Vec<Cell>;

struct State {
    cols: usize,
    rows: usize,
    screen: Vec<Row>,
    saved_primary: Option<(Vec<Row>, (usize, usize))>,
    scrollback: VecDeque<Row>,
    max_scrollback: usize,
    cursor: (usize, usize),
    saved_cursor: (usize, usize),
    attr: Attr,
    wrap_pending: bool,
    autowrap: bool,
    cursor_visible: bool,
    app_cursor_keys: bool,
    bracketed_paste: bool,
    scroll_top: usize,
    scroll_bottom: usize,
    title: String,
    bells: u64,
    responses: Vec<u8>,
}

/// Terminal emulator state. Feed PTY output with [`Terminal::feed`]; draw [`Terminal::visible`].
pub struct Terminal {
    parser: Parser,
    state: State,
    /// Lines scrolled back from the bottom (0 = live view).
    pub view_offset: usize,
}

impl Terminal {
    pub fn new(cols: usize, rows: usize) -> Self {
        let cols = cols.max(2);
        let rows = rows.max(1);
        Terminal {
            parser: Parser::new(),
            state: State {
                cols,
                rows,
                screen: vec![vec![Cell::default(); cols]; rows],
                saved_primary: None,
                scrollback: VecDeque::new(),
                max_scrollback: 10_000,
                cursor: (0, 0),
                saved_cursor: (0, 0),
                attr: Attr::default(),
                wrap_pending: false,
                autowrap: true,
                cursor_visible: true,
                app_cursor_keys: false,
                bracketed_paste: false,
                scroll_top: 0,
                scroll_bottom: rows - 1,
                title: String::new(),
                bells: 0,
                responses: Vec::new(),
            },
            view_offset: 0,
        }
    }

    pub fn feed(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.parser.advance(&mut self.state, b);
        }
        if self.view_offset > 0 && !bytes.is_empty() {
            self.view_offset = self.view_offset.min(self.state.scrollback.len());
        }
    }

    pub fn cols(&self) -> usize { self.state.cols }
    pub fn rows(&self) -> usize { self.state.rows }
    pub fn cursor(&self) -> (usize, usize) { self.state.cursor }
    pub fn cursor_visible(&self) -> bool { self.state.cursor_visible && self.view_offset == 0 }
    pub fn title(&self) -> &str { &self.state.title }
    pub fn bells(&self) -> u64 { self.state.bells }
    pub fn app_cursor_keys(&self) -> bool { self.state.app_cursor_keys }
    pub fn bracketed_paste(&self) -> bool { self.state.bracketed_paste }
    pub fn alternate_screen(&self) -> bool { self.state.saved_primary.is_some() }
    pub fn scrollback_len(&self) -> usize { self.state.scrollback.len() }

    /// Replies the host expects (DSR cursor position, DA). Send them back to the PTY.
    pub fn take_responses(&mut self) -> Vec<u8> { std::mem::take(&mut self.state.responses) }

    /// Rows currently on screen, honouring the scrollback view offset.
    pub fn visible(&self) -> Vec<&Row> {
        let s = &self.state;
        let offset = self.view_offset.min(s.scrollback.len());
        let mut rows: Vec<&Row> = Vec::with_capacity(s.rows);
        let start = s.scrollback.len() - offset;
        for r in s.scrollback.iter().skip(start).take(s.rows) {
            rows.push(r);
        }
        for r in s.screen.iter() {
            if rows.len() == s.rows { break; }
            rows.push(r);
        }
        rows
    }

    pub fn scroll_view(&mut self, lines: isize) {
        let max = self.state.scrollback.len() as isize;
        self.view_offset = (self.view_offset as isize + lines).clamp(0, max) as usize;
    }

    pub fn resize(&mut self, cols: usize, rows: usize) {
        let cols = cols.max(2);
        let rows = rows.max(1);
        let s = &mut self.state;
        if cols == s.cols && rows == s.rows { return; }
        for row in s.screen.iter_mut() { row.resize(cols, Cell::default()); }
        while s.screen.len() > rows {
            // Keep the cursor line visible: move surplus top lines into scrollback.
            if s.cursor.0 > 0 {
                let line = s.screen.remove(0);
                if s.saved_primary.is_none() { s.scrollback.push_back(line); }
                s.cursor.0 -= 1;
            } else {
                s.screen.pop();
            }
        }
        while s.screen.len() < rows {
            if s.saved_primary.is_none() {
                if let Some(mut line) = s.scrollback.pop_back() {
                    line.resize(cols, Cell::default());
                    s.screen.insert(0, line);
                    s.cursor.0 += 1;
                    continue;
                }
            }
            s.screen.push(vec![Cell::default(); cols]);
        }
        s.cols = cols;
        s.rows = rows;
        s.scroll_top = 0;
        s.scroll_bottom = rows - 1;
        s.cursor.0 = s.cursor.0.min(rows - 1);
        s.cursor.1 = s.cursor.1.min(cols - 1);
        s.wrap_pending = false;
        while s.scrollback.len() > s.max_scrollback { s.scrollback.pop_front(); }
    }

    /// Plain text of a line (scrollback lines first, then screen), trailing blanks trimmed.
    pub fn line_text(&self, index: usize) -> Option<String> {
        let s = &self.state;
        let row = if index < s.scrollback.len() { s.scrollback.get(index) } else { s.screen.get(index - s.scrollback.len()) }?;
        Some(row_text(row))
    }

    pub fn total_lines(&self) -> usize { self.state.scrollback.len() + self.state.rows }

    /// Text between two (line, column) positions in absolute line numbering.
    pub fn selection_text(&self, from: (usize, usize), to: (usize, usize)) -> String {
        let (a, b) = if from <= to { (from, to) } else { (to, from) };
        let mut out = String::new();
        for line in a.0..=b.0 {
            let Some(text) = self.line_text(line) else { break };
            let chars: Vec<char> = text.chars().collect();
            let start = if line == a.0 { a.1.min(chars.len()) } else { 0 };
            let end = if line == b.0 { (b.1 + 1).min(chars.len()) } else { chars.len() };
            if start < end { out.extend(&chars[start..end]); }
            if line != b.0 { out.push('\n'); }
        }
        out
    }

    /// Case-insensitive search over scrollback and screen; returns (line, column) of each match.
    pub fn find(&self, needle: &str) -> Vec<(usize, usize)> {
        let mut hits = Vec::new();
        if needle.is_empty() { return hits; }
        let needle = needle.to_lowercase();
        for line in 0..self.total_lines() {
            if let Some(text) = self.line_text(line) {
                let hay = text.to_lowercase();
                let mut start = 0;
                while let Some(pos) = hay[start..].find(&needle) {
                    let byte = start + pos;
                    hits.push((line, hay[..byte].chars().count()));
                    start = byte + needle.len().max(1);
                }
            }
        }
        hits
    }

    /// Screen as text (tests and diagnostics).
    pub fn screen_text(&self) -> String {
        self.state.screen.iter().map(row_text).collect::<Vec<_>>().join("\n")
    }

    pub fn cell(&self, row: usize, col: usize) -> Option<Cell> {
        self.state.screen.get(row).and_then(|r| r.get(col)).copied()
    }

    /// Keyboard to bytes for special keys (respects DECCKM application cursor mode).
    pub fn key_sequence(&self, key: Key) -> &'static [u8] {
        let app = self.state.app_cursor_keys;
        match key {
            Key::Up => if app { b"\x1bOA" } else { b"\x1b[A" },
            Key::Down => if app { b"\x1bOB" } else { b"\x1b[B" },
            Key::Right => if app { b"\x1bOC" } else { b"\x1b[C" },
            Key::Left => if app { b"\x1bOD" } else { b"\x1b[D" },
            Key::Home => b"\x1b[H",
            Key::End => b"\x1b[F",
            Key::PageUp => b"\x1b[5~",
            Key::PageDown => b"\x1b[6~",
            Key::Insert => b"\x1b[2~",
            Key::Delete => b"\x1b[3~",
            Key::F(1) => b"\x1bOP", Key::F(2) => b"\x1bOQ", Key::F(3) => b"\x1bOR", Key::F(4) => b"\x1bOS",
            Key::F(5) => b"\x1b[15~", Key::F(6) => b"\x1b[17~", Key::F(7) => b"\x1b[18~", Key::F(8) => b"\x1b[19~",
            Key::F(9) => b"\x1b[20~", Key::F(10) => b"\x1b[21~", Key::F(11) => b"\x1b[23~", Key::F(12) => b"\x1b[24~",
            Key::F(_) => b"",
        }
    }

    /// Wraps pasted text in bracketed-paste markers when the application asked for them.
    pub fn paste_bytes(&self, text: &str) -> Vec<u8> {
        let clean = text.replace("\r\n", "\r").replace('\n', "\r");
        if self.state.bracketed_paste {
            let mut v = b"\x1b[200~".to_vec();
            v.extend(clean.replace("\x1b[201~", "").as_bytes());
            v.extend(b"\x1b[201~");
            v
        } else {
            clean.into_bytes()
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Key { Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete, F(u8) }

fn row_text(row: &Row) -> String {
    let s: String = row.iter().filter(|c| !c.spacer).map(|c| c.ch).collect();
    s.trim_end().to_string()
}

impl State {
    fn blank(&self) -> Cell { Cell { ch: ' ', attr: Attr { fg: Color::Default, bg: self.attr.bg, ..Attr::default() }, spacer: false } }

    fn scroll_up(&mut self, n: usize) {
        for _ in 0..n {
            let line = self.screen.remove(self.scroll_top);
            if self.scroll_top == 0 && self.saved_primary.is_none() {
                self.scrollback.push_back(line);
                if self.scrollback.len() > self.max_scrollback { self.scrollback.pop_front(); }
            }
            let blank = vec![self.blank(); self.cols];
            self.screen.insert(self.scroll_bottom, blank);
        }
    }

    fn scroll_down(&mut self, n: usize) {
        for _ in 0..n {
            self.screen.remove(self.scroll_bottom);
            let blank = vec![self.blank(); self.cols];
            self.screen.insert(self.scroll_top, blank);
        }
    }

    fn linefeed(&mut self) {
        self.wrap_pending = false;
        if self.cursor.0 == self.scroll_bottom { self.scroll_up(1); } else if self.cursor.0 + 1 < self.rows { self.cursor.0 += 1; }
    }

    fn reverse_index(&mut self) {
        if self.cursor.0 == self.scroll_top { self.scroll_down(1); } else if self.cursor.0 > 0 { self.cursor.0 -= 1; }
    }

    fn put(&mut self, ch: char, width: usize) {
        if self.wrap_pending && self.autowrap {
            self.cursor.1 = 0;
            self.linefeed();
        }
        if self.cursor.1 + width > self.cols {
            if self.autowrap { self.cursor.1 = 0; self.linefeed(); } else { self.cursor.1 = self.cols - width; }
        }
        let (r, c) = self.cursor;
        self.screen[r][c] = Cell { ch, attr: self.attr, spacer: false };
        if width == 2 && c + 1 < self.cols { self.screen[r][c + 1] = Cell { ch: ' ', attr: self.attr, spacer: true }; }
        if c + width >= self.cols { self.cursor.1 = self.cols - 1; self.wrap_pending = true; } else { self.cursor.1 = c + width; }
    }

    fn erase(&mut self, row: usize, from: usize, to: usize) {
        let blank = self.blank();
        if let Some(line) = self.screen.get_mut(row) {
            let end = to.min(line.len());
            for cell in line.iter_mut().take(end).skip(from) { *cell = blank; }
        }
    }

    fn set_alternate(&mut self, on: bool) {
        if on && self.saved_primary.is_none() {
            let screen = std::mem::replace(&mut self.screen, vec![vec![Cell::default(); self.cols]; self.rows]);
            self.saved_primary = Some((screen, self.cursor));
            self.cursor = (0, 0);
        } else if !on {
            if let Some((screen, cursor)) = self.saved_primary.take() {
                self.screen = screen;
                self.screen.resize(self.rows, vec![Cell::default(); self.cols]);
                for row in self.screen.iter_mut() { row.resize(self.cols, Cell::default()); }
                self.cursor = (cursor.0.min(self.rows - 1), cursor.1.min(self.cols - 1));
            }
        }
        self.wrap_pending = false;
    }

    fn sgr(&mut self, params: &Params) {
        let flat: Vec<Vec<u16>> = params.iter().map(|p| p.to_vec()).collect();
        if flat.is_empty() { self.attr = Attr::default(); return; }
        let mut i = 0;
        while i < flat.len() {
            let p = &flat[i];
            let code = p.first().copied().unwrap_or(0);
            match code {
                0 => self.attr = Attr::default(),
                1 => self.attr.bold = true,
                3 => self.attr.italic = true,
                4 => self.attr.underline = true,
                7 => self.attr.inverse = true,
                22 => self.attr.bold = false,
                23 => self.attr.italic = false,
                24 => self.attr.underline = false,
                27 => self.attr.inverse = false,
                30..=37 => self.attr.fg = Color::Indexed((code - 30) as u8),
                39 => self.attr.fg = Color::Default,
                40..=47 => self.attr.bg = Color::Indexed((code - 40) as u8),
                49 => self.attr.bg = Color::Default,
                90..=97 => self.attr.fg = Color::Indexed((code - 90 + 8) as u8),
                100..=107 => self.attr.bg = Color::Indexed((code - 100 + 8) as u8),
                38 | 48 => {
                    // Either colon subparameters (38:5:n / 38:2:r:g:b) or separate params (38;5;n).
                    let (color, consumed) = if p.len() > 1 {
                        (extended_color(&p[1..]), 0)
                    } else {
                        let rest: Vec<u16> = flat[i + 1..].iter().map(|x| x.first().copied().unwrap_or(0)).collect();
                        let used = match rest.first() { Some(5) => 2, Some(2) => 4, _ => 0 };
                        (extended_color(&rest), used)
                    };
                    if let Some(color) = color { if code == 38 { self.attr.fg = color } else { self.attr.bg = color } }
                    i += consumed;
                }
                _ => {}
            }
            i += 1;
        }
    }
}

fn extended_color(p: &[u16]) -> Option<Color> {
    match p.first() {
        Some(5) => p.get(1).map(|n| Color::Indexed((*n).min(255) as u8)),
        Some(2) => {
            // 38:2::r:g:b carries an optional colour-space id.
            let v: Vec<u16> = if p.len() >= 5 { p[2..5].to_vec() } else { p[1..].to_vec() };
            if v.len() >= 3 { Some(Color::Rgb(v[0].min(255) as u8, v[1].min(255) as u8, v[2].min(255) as u8)) } else { None }
        }
        _ => None,
    }
}

impl Perform for State {
    fn print(&mut self, c: char) {
        match c.width() {
            Some(0) | None => {} // combining marks and controls: not stored in this prototype grid
            Some(w) => self.put(c, w.min(2)),
        }
    }

    fn execute(&mut self, byte: u8) {
        match byte {
            0x07 => self.bells += 1,
            0x08 => { self.wrap_pending = false; self.cursor.1 = self.cursor.1.saturating_sub(1); }
            0x09 => { let next = (self.cursor.1 / 8 + 1) * 8; self.cursor.1 = next.min(self.cols - 1); }
            0x0A..=0x0C => self.linefeed(),
            0x0D => { self.cursor.1 = 0; self.wrap_pending = false; }
            _ => {}
        }
    }

    fn osc_dispatch(&mut self, params: &[&[u8]], _bell_terminated: bool) {
        // OSC 0/2 window title. OSC 52 (clipboard) is deliberately ignored.
        if params.len() >= 2 && (params[0] == b"0" || params[0] == b"2") {
            self.title = String::from_utf8_lossy(params[1]).chars().take(256).collect();
        }
    }

    fn csi_dispatch(&mut self, params: &Params, intermediates: &[u8], _ignore: bool, action: char) {
        let private = intermediates.first() == Some(&b'?');
        let values: Vec<usize> = params.iter().map(|p| p.first().copied().unwrap_or(0) as usize).collect();
        let arg = |i: usize, default: usize| -> usize { match values.get(i) { Some(0) | None => default, Some(v) => *v } };
        let rows = self.rows;
        let cols = self.cols;
        match (private, action) {
            (false, 'A') => { self.cursor.0 = self.cursor.0.saturating_sub(arg(0, 1)).max(if self.cursor.0 >= self.scroll_top { self.scroll_top } else { 0 }); self.wrap_pending = false; }
            (false, 'B') | (false, 'e') => { self.cursor.0 = (self.cursor.0 + arg(0, 1)).min(if self.cursor.0 <= self.scroll_bottom { self.scroll_bottom } else { rows - 1 }); self.wrap_pending = false; }
            (false, 'C') | (false, 'a') => { self.cursor.1 = (self.cursor.1 + arg(0, 1)).min(cols - 1); self.wrap_pending = false; }
            (false, 'D') => { self.cursor.1 = self.cursor.1.saturating_sub(arg(0, 1)); self.wrap_pending = false; }
            (false, 'E') => { self.cursor.0 = (self.cursor.0 + arg(0, 1)).min(rows - 1); self.cursor.1 = 0; }
            (false, 'F') => { self.cursor.0 = self.cursor.0.saturating_sub(arg(0, 1)); self.cursor.1 = 0; }
            (false, 'G') | (false, '`') => { self.cursor.1 = (arg(0, 1) - 1).min(cols - 1); self.wrap_pending = false; }
            (false, 'd') => { self.cursor.0 = (arg(0, 1) - 1).min(rows - 1); self.wrap_pending = false; }
            (false, 'H') | (false, 'f') => { self.cursor = ((arg(0, 1) - 1).min(rows - 1), (arg(1, 1) - 1).min(cols - 1)); self.wrap_pending = false; }
            (false, 'J') => {
                let (r, c) = self.cursor;
                match values.first().copied().unwrap_or(0) {
                    0 => { self.erase(r, c, cols); for row in r + 1..rows { self.erase(row, 0, cols); } }
                    1 => { for row in 0..r { self.erase(row, 0, cols); } self.erase(r, 0, c + 1); }
                    2 => { for row in 0..rows { self.erase(row, 0, cols); } }
                    3 => self.scrollback.clear(),
                    _ => {}
                }
            }
            (false, 'K') => {
                let (r, c) = self.cursor;
                match values.first().copied().unwrap_or(0) { 0 => self.erase(r, c, cols), 1 => self.erase(r, 0, c + 1), 2 => self.erase(r, 0, cols), _ => {} }
            }
            (false, 'L') => {
                if (self.scroll_top..=self.scroll_bottom).contains(&self.cursor.0) {
                    for _ in 0..arg(0, 1) { self.screen.remove(self.scroll_bottom); let b = vec![self.blank(); cols]; self.screen.insert(self.cursor.0, b); }
                }
            }
            (false, 'M') => {
                if (self.scroll_top..=self.scroll_bottom).contains(&self.cursor.0) {
                    for _ in 0..arg(0, 1) { self.screen.remove(self.cursor.0); let b = vec![self.blank(); cols]; self.screen.insert(self.scroll_bottom, b); }
                }
            }
            (false, 'P') => {
                let (r, c) = self.cursor; let n = arg(0, 1).min(cols - c); let blank = self.blank();
                let line = &mut self.screen[r]; for _ in 0..n { line.remove(c); line.push(blank); }
            }
            (false, '@') => {
                let (r, c) = self.cursor; let n = arg(0, 1).min(cols - c); let blank = self.blank();
                let line = &mut self.screen[r]; for _ in 0..n { line.insert(c, blank); line.pop(); }
            }
            (false, 'X') => { let (r, c) = self.cursor; self.erase(r, c, (c + arg(0, 1)).min(cols)); }
            (false, 'S') => self.scroll_up(arg(0, 1)),
            (false, 'T') => self.scroll_down(arg(0, 1)),
            (false, 'r') => {
                let top = arg(0, 1) - 1; let bottom = arg(1, rows) - 1;
                if top < bottom && bottom < rows { self.scroll_top = top; self.scroll_bottom = bottom; self.cursor = (0, 0); }
            }
            (false, 'm') => self.sgr(params),
            (false, 's') => self.saved_cursor = self.cursor,
            (false, 'u') => { self.cursor = self.saved_cursor; self.wrap_pending = false; }
            (false, 'n') => match values.first().copied().unwrap_or(0) {
                5 => self.responses.extend_from_slice(b"\x1b[0n"),
                6 => self.responses.extend(format!("\x1b[{};{}R", self.cursor.0 + 1, self.cursor.1 + 1).into_bytes()),
                _ => {}
            },
            (false, 'c') => self.responses.extend_from_slice(b"\x1b[?1;2c"),
            (true, 'h') | (true, 'l') => {
                let on = action == 'h';
                for mode in &values {
                    match mode {
                        1 => self.app_cursor_keys = on,
                        7 => self.autowrap = on,
                        25 => self.cursor_visible = on,
                        47 | 1047 => self.set_alternate(on),
                        1049 => { if on { self.saved_cursor = self.cursor; } self.set_alternate(on); if !on { self.cursor = self.saved_cursor; } }
                        2004 => self.bracketed_paste = on,
                        _ => {}
                    }
                }
            }
            _ => {}
        }
    }

    fn esc_dispatch(&mut self, intermediates: &[u8], _ignore: bool, byte: u8) {
        if !intermediates.is_empty() { return; }
        match byte {
            b'7' => self.saved_cursor = self.cursor,
            b'8' => { self.cursor = self.saved_cursor; self.wrap_pending = false; }
            b'D' => self.linefeed(),
            b'E' => { self.cursor.1 = 0; self.linefeed(); }
            b'M' => self.reverse_index(),
            b'c' => {
                let (cols, rows) = (self.cols, self.rows);
                self.screen = vec![vec![Cell::default(); cols]; rows];
                self.cursor = (0, 0); self.attr = Attr::default(); self.scroll_top = 0; self.scroll_bottom = rows - 1; self.saved_primary = None;
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prints_wraps_and_scrolls_into_scrollback() {
        let mut t = Terminal::new(5, 2);
        t.feed(b"abcdefgh\r\nxy");
        assert_eq!(t.screen_text(), "fgh\nxy");
        assert_eq!(t.line_text(0).as_deref(), Some("abcde"));
    }

    #[test]
    fn cursor_moves_erase_and_sgr_colours() {
        let mut t = Terminal::new(10, 3);
        t.feed(b"\x1b[2;3H\x1b[1;31mX\x1b[38;5;196mY\x1b[38:2::1:2:3mZ\x1b[48;2;4;5;6mW\x1b[0m");
        assert_eq!(t.cell(1, 2).unwrap().attr.fg, Color::Indexed(1));
        assert!(t.cell(1, 2).unwrap().attr.bold);
        assert_eq!(t.cell(1, 3).unwrap().attr.fg, Color::Indexed(196));
        assert_eq!(t.cell(1, 4).unwrap().attr.fg, Color::Rgb(1, 2, 3));
        assert_eq!(t.cell(1, 5).unwrap().attr.bg, Color::Rgb(4, 5, 6));
        t.feed(b"\x1b[2;1H\x1b[K");
        assert_eq!(t.screen_text(), "\n\n");
    }

    #[test]
    fn alternate_screen_restores_primary_like_vim_or_top() {
        let mut t = Terminal::new(10, 3);
        t.feed(b"shell$ ");
        t.feed(b"\x1b[?1049h\x1b[2J\x1b[Htop screen");
        assert!(t.alternate_screen());
        assert_eq!(t.screen_text().lines().next(), Some("top screen"));
        t.feed(b"\x1b[?1049l");
        assert!(!t.alternate_screen());
        assert_eq!(t.screen_text().lines().next(), Some("shell$"));
        assert_eq!(t.cursor(), (0, 7));
    }

    #[test]
    fn wide_and_cyrillic_characters_take_correct_cells() {
        let mut t = Terminal::new(10, 1);
        t.feed("Привет界".as_bytes());
        assert_eq!(t.cursor(), (0, 8));
        assert!(t.cell(0, 7).unwrap().spacer);
        assert_eq!(t.screen_text(), "Привет界");
    }

    #[test]
    fn dsr_reply_resize_search_and_selection() {
        let mut t = Terminal::new(20, 4);
        t.feed(b"one\r\ntwo needle\r\n\x1b[6n");
        assert_eq!(t.take_responses(), b"\x1b[3;1R".to_vec());
        assert_eq!(t.find("NEEDLE"), vec![(1, 4)]);
        assert_eq!(t.selection_text((0, 0), (1, 2)), "one\ntwo");
        t.resize(8, 2);
        assert_eq!(t.cols(), 8);
        assert!(t.total_lines() >= 3);
    }

    #[test]
    fn scroll_region_and_insert_delete_lines() {
        let mut t = Terminal::new(4, 4);
        t.feed(b"1\r\n2\r\n3\r\n4\x1b[2;3r\x1b[2;1H\x1b[M");
        assert_eq!(t.screen_text(), "1\n3\n\n4");
        t.feed(b"\x1b[2;1H\x1b[L");
        assert_eq!(t.screen_text(), "1\n\n3\n4");
    }

    #[test]
    fn bracketed_paste_and_application_cursor_keys() {
        let mut t = Terminal::new(10, 2);
        assert_eq!(t.key_sequence(Key::Up), b"\x1b[A");
        t.feed(b"\x1b[?1h\x1b[?2004h");
        assert_eq!(t.key_sequence(Key::Up), b"\x1bOA");
        assert_eq!(t.paste_bytes("a\nb"), b"\x1b[200~a\rb\x1b[201~".to_vec());
    }

    #[test]
    fn large_output_throughput_is_bounded_by_scrollback() {
        let mut t = Terminal::new(120, 40);
        let line = b"\x1b[32mINFO\x1b[0m 2026-11-04 12:00:00 service started on port 8080 \xd0\xbf\xd1\x80\xd0\xb8\r\n";
        let start = std::time::Instant::now();
        let mut bytes = 0;
        for _ in 0..50_000 { t.feed(line); bytes += line.len(); }
        let elapsed = start.elapsed();
        assert!(t.scrollback_len() <= 10_000);
        println!("terminal throughput: {:.1} MB/s ({} bytes in {:?})", bytes as f64 / elapsed.as_secs_f64() / 1e6, bytes, elapsed);
    }
}
