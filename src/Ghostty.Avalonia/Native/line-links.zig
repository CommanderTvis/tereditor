//! The host-resolved file links Ghostty's renderer underlines, kept by the text of their logical line. The build copies
//! this file to src/renderer/gav_line_links.zig (see LinkUnderlines.patch). It imports only std, so its tests run
//! alone: `zig test line-links.zig`. The caller holds the renderer state mutex for push and place, and `pages` is the
//! active screen's PageList.
const std = @import("std");

/// The viewport index of a cell outside the viewport.
const hidden = std.math.maxInt(u32);
/// The codes of the cells without text of their own: a wide character's right half, and the blank a wide character
/// leaves at a row's end when it wraps.
const tail = 0;
const head = std.math.maxInt(u32);
/// Set on a pushed codepoint whose cell is to be underlined.
const linked_bit: u32 = 1 << 31;
/// A viewport cell the host did not push.
const unread = std.math.maxInt(u32);

pub const LineLinks = struct {
    /// Lines the host resolved: the key is the line's cell codes (see read) as bytes, the value the indexes of its
    /// cells to underline, none for a line without links. Every frame looks up the visible lines' own text, so an
    /// underline moves with its line through scrolling, output and reflow while that text is unchanged.
    known: std.StringHashMapUnmanaged([]u32) = .empty,
    /// The known lines that have links.
    linked: usize = 0,
    /// The names on each line of the last resolution of the viewport. While such a line is not shown, an unknown line
    /// underlines those of its names it shows whole, so the line being edited keeps its underlines.
    donors: std.ArrayListUnmanaged(Donor) = .empty,
    /// Scratch: the line last read, with each cell's viewport index, and the unknown lines of a frame.
    codes: std.ArrayListUnmanaged(u32) = .empty,
    cells: std.ArrayListUnmanaged(u32) = .empty,
    unknown_codes: std.ArrayListUnmanaged(u32) = .empty,
    unknown_cells: std.ArrayListUnmanaged(u32) = .empty,
    unknown_ends: std.ArrayListUnmanaged(usize) = .empty,
    lending: std.ArrayListUnmanaged([]const u32) = .empty,

    const Donor = struct {
        hash: u64,
        /// The names' codepoints, each name ended by a 0.
        names: []u32,
        shown: bool = false,
    };

    pub fn deinit(self: *LineLinks, alloc: std.mem.Allocator) void {
        self.clear(alloc);
        self.known.deinit(alloc);
        self.donors.deinit(alloc);
        self.codes.deinit(alloc);
        self.cells.deinit(alloc);
        self.unknown_codes.deinit(alloc);
        self.unknown_cells.deinit(alloc);
        self.unknown_ends.deinit(alloc);
        self.lending.deinit(alloc);
    }

    /// Attaches resolved links to the visible logical lines. `cells` are the viewport indexes (row * columns + column)
    /// of every cell the host read and `codepoints` the base codepoint each showed, with linked_bit on the cells to
    /// underline. A line that no longer shows what was read is skipped. With `mode` 1 the host resolved what it read:
    /// each line read takes these links, also none, and the lines with links become the donors. With 0 the links only
    /// repeat earlier ones, so a line without them keeps what it has. With 2 everything is dropped first.
    pub fn push(self: *LineLinks, alloc: std.mem.Allocator, pages: anytype, cells: []const u32, codepoints: []const u32, mode: u32) !void {
        if (mode == 2) self.clear(alloc);
        // Bounded: forgotten lines resolve again, and this push restores the visible ones it carries.
        if (self.known.count() + pages.rows > 4096) self.clearKnown(alloc);
        // The pushed codepoint of each viewport cell.
        const shown = try alloc.alloc(u32, @as(usize, pages.rows) * pages.cols);
        defer alloc.free(shown);
        @memset(shown, unread);
        for (cells, codepoints) |cell, codepoint| if (cell < shown.len) {
            shown[cell] = codepoint;
        };
        var marks: std.ArrayListUnmanaged(u32) = .empty;
        defer marks.deinit(alloc);
        var donors: std.ArrayListUnmanaged(Donor) = .empty;
        defer {
            for (donors.items) |donor| alloc.free(donor.names);
            donors.deinit(alloc);
        }
        var stale = false;
        var row: u32 = 0;
        while (row < pages.rows) {
            row = try self.read(alloc, pages, row);
            marks.clearRetainingCapacity();
            var same: usize = 0;
            var other: usize = 0;
            var missing: usize = 0;
            var whole = true;
            for (self.cells.items, self.codes.items, 0..) |cell, code, index| {
                if (cell == hidden) {
                    whole = false;
                    continue;
                }
                if (gap(code)) continue;
                const value = shown[cell];
                if (value == unread) {
                    missing += 1;
                    continue;
                }
                if (value & ~linked_bit != code) {
                    other += 1;
                    continue;
                }
                same += 1;
                if (value & linked_bit != 0) try marks.append(alloc, @intCast(index));
            }
            // A line the host did not read at all lies past the end of a text cut short.
            if (same == 0 and other == 0) continue;
            if (other != 0 or missing != 0) {
                stale = true;
                continue;
            }
            const key = self.lineKey();
            if (key.len == 0) continue;
            if (marks.items.len > 0) {
                try self.set(alloc, key, marks.items);
                if (mode == 1) try self.addDonor(alloc, &donors, key, marks.items);
            } else if (mode != 0) {
                // The start of a line scrolled partly away was not resolved: it is not known to have no links.
                if (whole) try self.set(alloc, key, marks.items) else self.remove(alloc, key);
            }
        }
        // Output that arrived since the host read the viewport leaves the earlier donors.
        if (mode == 1 and !stale) std.mem.swap(std.ArrayListUnmanaged(Donor), &self.donors, &donors);
    }

    /// Adds the viewport cells to underline in the frame showing `pages` to `out`.
    pub fn place(self: *LineLinks, alloc: std.mem.Allocator, pages: anytype, out: *std.AutoHashMapUnmanaged(u32, void)) !void {
        if (self.linked == 0 and self.donors.items.len == 0) return;
        for (self.donors.items) |*donor| donor.shown = false;
        self.unknown_codes.clearRetainingCapacity();
        self.unknown_cells.clearRetainingCapacity();
        self.unknown_ends.clearRetainingCapacity();
        var row: u32 = 0;
        while (row < pages.rows) {
            row = try self.read(alloc, pages, row);
            const key = self.lineKey();
            if (self.donors.items.len > 0) {
                const hash = std.hash.Wyhash.hash(0, key);
                for (self.donors.items) |*donor| if (!donor.shown and donor.hash == hash) {
                    donor.shown = true;
                    break;
                };
            }
            if (self.known.get(key)) |marks| {
                for (marks) |mark| if (mark < self.cells.items.len) try underline(alloc, out, self.codes.items, self.cells.items, mark);
            } else if (self.donors.items.len > 0 and key.len > 0) {
                try self.unknown_codes.appendSlice(alloc, self.codes.items);
                try self.unknown_cells.appendSlice(alloc, self.cells.items);
                try self.unknown_ends.append(alloc, self.unknown_codes.items.len);
            }
        }
        if (self.unknown_ends.items.len == 0) return;
        // A donor still shown lends nothing: new output repeating its name waits for its own resolution.
        self.lending.clearRetainingCapacity();
        for (self.donors.items) |donor| {
            if (donor.shown) continue;
            var names = std.mem.tokenizeScalar(u32, donor.names, 0);
            while (names.next()) |found| try self.lending.append(alloc, found);
        }
        if (self.lending.items.len == 0) return;
        var start: usize = 0;
        for (self.unknown_ends.items) |end| {
            try self.carry(alloc, out, self.unknown_codes.items[start..end], self.unknown_cells.items[start..end]);
            start = end;
        }
    }

    /// Reads the logical line through viewport row `row` (from its first wrapped row, possibly in scrollback, to its
    /// last) into codes, one entry per cell: the base codepoint, a space for a blank cell, tail or head; and into cells
    /// each cell's viewport index, or hidden outside the viewport. Returns the first viewport row after the line.
    fn read(self: *LineLinks, alloc: std.mem.Allocator, pages: anytype, row: u32) !u32 {
        self.codes.clearRetainingCapacity();
        self.cells.clearRetainingCapacity();
        var pin = pages.pin(.{ .viewport = .{ .y = @intCast(row) } }) orelse return row + 1;
        var y: i64 = row;
        var up: usize = 0;
        while (up < 64) : (up += 1) {
            const above = pin.up(1) orelse break;
            if (!above.rowAndCell().row.wrap) break;
            pin = above;
            y -= 1;
        }
        var down: usize = 0;
        while (true) : (down += 1) {
            const visible = y >= 0 and y < pages.rows;
            for (pin.cells(.all), 0..) |cell, x| {
                const code: u32 = if (cell.wide == .spacer_tail) tail else if (cell.wide == .spacer_head) head else if (cell.codepoint() == 0) ' ' else cell.codepoint();
                try self.codes.append(alloc, code);
                try self.cells.append(alloc, if (visible) @intCast(y * pages.cols + @as(i64, @intCast(x))) else hidden);
            }
            if (!pin.rowAndCell().row.wrap or down >= 128) break;
            pin = pin.down(1) orelse break;
            y += 1;
        }
        return @intCast(@max(y + 1, @as(i64, row) + 1));
    }

    /// The key of the line last read: its cells without trailing blanks.
    fn lineKey(self: *LineLinks) []const u8 {
        var len = self.codes.items.len;
        while (len > 0 and self.codes.items[len - 1] == ' ') len -= 1;
        return std.mem.sliceAsBytes(self.codes.items[0..len]);
    }

    fn set(self: *LineLinks, alloc: std.mem.Allocator, key: []const u8, marks: []const u32) !void {
        const value = try alloc.dupe(u32, marks);
        errdefer alloc.free(value);
        const entry = try self.known.getOrPut(alloc, key);
        if (entry.found_existing) {
            if (entry.value_ptr.len > 0) self.linked -= 1;
            alloc.free(entry.value_ptr.*);
        } else {
            entry.key_ptr.* = alloc.dupe(u8, key) catch |err| {
                _ = self.known.remove(key);
                return err;
            };
        }
        entry.value_ptr.* = value;
        if (value.len > 0) self.linked += 1;
    }

    fn remove(self: *LineLinks, alloc: std.mem.Allocator, key: []const u8) void {
        const old = self.known.fetchRemove(key) orelse return;
        if (old.value.len > 0) self.linked -= 1;
        alloc.free(old.key);
        alloc.free(old.value);
    }

    /// Records the names on the line last read: each run of marked cells, unless the viewport's edge cut it.
    fn addDonor(self: *LineLinks, alloc: std.mem.Allocator, donors: *std.ArrayListUnmanaged(Donor), key: []const u8, marks: []const u32) !void {
        var names: std.ArrayListUnmanaged(u32) = .empty;
        defer names.deinit(alloc);
        const codes = self.codes.items;
        var first: usize = 0;
        while (first < marks.len) {
            var last = first;
            while (last + 1 < marks.len and following(codes[0..marks[last + 1]], marks[last] + 1) == null) last += 1;
            const before = previous(codes, marks[first]);
            const after = following(codes, marks[last] + 1);
            const cut = (before != null and self.cells.items[before.?] == hidden and !delimiter(codes[before.?])) or
                (after != null and self.cells.items[after.?] == hidden and !delimiter(codes[after.?]));
            if (!cut) {
                for (marks[first .. last + 1]) |mark| try names.append(alloc, codes[mark]);
                try names.append(alloc, 0);
            }
            first = last + 1;
        }
        if (names.items.len == 0) return;
        try donors.ensureUnusedCapacity(alloc, 1);
        donors.appendAssumeCapacity(.{ .hash = std.hash.Wyhash.hash(0, key), .names = try names.toOwnedSlice(alloc) });
    }

    /// Underlines each place the line shows a lent name whole: bounded by the line's ends or the delimiters the host's
    /// file scanner ends a name at, optionally followed by periods.
    fn carry(self: *LineLinks, alloc: std.mem.Allocator, out: *std.AutoHashMapUnmanaged(u32, void), codes: []const u32, cells: []const u32) !void {
        // Whether a name may start at the next cell with text: nothing but a delimiter precedes it.
        var bounded = true;
        for (codes, 0..) |code, start| {
            if (gap(code)) continue;
            defer bounded = delimiter(code);
            if (!bounded) continue;
            for (self.lending.items) |name| {
                if (code != name[0]) continue;
                var end = start;
                var matched: usize = 0;
                while (matched < name.len and end < codes.len) : (end += 1) {
                    if (gap(codes[end])) continue;
                    if (codes[end] != name[matched]) break;
                    matched += 1;
                }
                if (matched < name.len) continue;
                var next = end;
                while (next < codes.len and (gap(codes[next]) or codes[next] == '.')) next += 1;
                if (next < codes.len and !delimiter(codes[next])) continue;
                for (start..end) |index| if (!gap(codes[index])) try underline(alloc, out, codes, cells, index);
            }
        }
    }

    fn underline(alloc: std.mem.Allocator, out: *std.AutoHashMapUnmanaged(u32, void), codes: []const u32, cells: []const u32, index: usize) !void {
        if (cells[index] != hidden) try out.put(alloc, cells[index], {});
        if (index + 1 < codes.len and codes[index + 1] == tail and cells[index + 1] != hidden)
            try out.put(alloc, cells[index + 1], {});
    }

    fn clearKnown(self: *LineLinks, alloc: std.mem.Allocator) void {
        var it = self.known.iterator();
        while (it.next()) |entry| {
            alloc.free(entry.key_ptr.*);
            alloc.free(entry.value_ptr.*);
        }
        self.known.clearRetainingCapacity();
        self.linked = 0;
    }

    fn clear(self: *LineLinks, alloc: std.mem.Allocator) void {
        self.clearKnown(alloc);
        for (self.donors.items) |donor| alloc.free(donor.names);
        self.donors.clearRetainingCapacity();
    }

    fn gap(code: u32) bool {
        return code == tail or code == head;
    }

    /// The index of the last cell with text before `index`.
    fn previous(codes: []const u32, index: usize) ?usize {
        var i = index;
        while (i > 0) {
            i -= 1;
            if (!gap(codes[i])) return i;
        }
        return null;
    }

    /// The index of the first cell with text from `index` on.
    fn following(codes: []const u32, index: usize) ?usize {
        for (index..codes.len) |i| if (!gap(codes[i])) return i;
        return null;
    }

    /// White space, as .NET's char.IsWhiteSpace counts it, and the other characters TerminalLinkHighlights.Delimiter
    /// lists.
    fn delimiter(code: u32) bool {
        return switch (code) {
            0x09...0x0D, ' ', 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => true,
            '(', ')', '[', ']', '<', '>', '{', '}', '"', '\'', '`', ':', ';', ',', '=', '|', '^', '!', '?', '*' => true,
            else => false,
        };
    }
};

// A stand-in for the PageList: rows of cells with wrap flags, of which the last `rows` are the viewport.
const Fake = struct {
    const Wide = enum { narrow, wide, spacer_tail, spacer_head };
    const Cell = struct {
        value: u21 = 0,
        wide: Wide = .narrow,
        fn codepoint(self: Cell) u21 {
            return self.value;
        }
    };
    const Row = struct { wrap: bool = false };
    const Pin = struct {
        fake: *const Fake,
        y: usize,
        fn up(self: Pin, n: usize) ?Pin {
            return if (self.y < n) null else .{ .fake = self.fake, .y = self.y - n };
        }
        fn down(self: Pin, n: usize) ?Pin {
            return if (self.y + n >= self.fake.count) null else .{ .fake = self.fake, .y = self.y + n };
        }
        fn rowAndCell(self: Pin) struct { row: *const Row } {
            return .{ .row = &self.fake.flags[self.y] };
        }
        fn cells(self: Pin, _: enum { all }) []const Cell {
            return self.fake.grid[self.y * self.fake.cols ..][0..self.fake.cols];
        }
    };

    cols: u32 = 24,
    rows: u32 = 4,
    count: usize = 0,
    grid: [24 * 16]Cell = @splat(.{}),
    flags: [16]Row = @splat(.{}),

    /// Writes lines separated by newlines, soft-wrapping them; U+4E00 and above are wide.
    fn show(text: []const u8) Fake {
        var fake: Fake = .{};
        var x: u32 = 0;
        var view = std.unicode.Utf8View.initUnchecked(text).iterator();
        while (view.nextCodepoint()) |c| {
            if (c == '\n') {
                fake.count += 1;
                x = 0;
                continue;
            }
            const width: u32 = if (c >= 0x4E00) 2 else 1;
            if (x + width > fake.cols) {
                if (x < fake.cols) fake.grid[fake.count * fake.cols + x].wide = .spacer_head;
                fake.flags[fake.count].wrap = true;
                fake.count += 1;
                x = 0;
            }
            fake.grid[fake.count * fake.cols + x] = .{ .value = c, .wide = if (width == 2) .wide else .narrow };
            if (width == 2) fake.grid[fake.count * fake.cols + x + 1].wide = .spacer_tail;
            x += width;
        }
        fake.count = @max(fake.count + 1, fake.rows);
        return fake;
    }

    fn pin(self: *const Fake, point: struct { viewport: struct { y: u32 = 0 } }) ?Pin {
        return .{ .fake = self, .y = self.count - self.rows + point.viewport.y };
    }

    /// Pushes what the host would after reading this screen: every visible cell, linking the names it shows.
    fn resolve(self: *const Fake, links: *LineLinks, names: []const []const u8, mode: u32) !void {
        var cells: [24 * 4]u32 = undefined;
        var codes: [24 * 4]u32 = undefined;
        var count: usize = 0;
        const top = self.count - self.rows;
        for (0..self.rows * self.cols) |i| {
            const cell = self.grid[top * self.cols + i];
            if (cell.wide == .spacer_tail or cell.wide == .spacer_head) continue;
            cells[count] = @intCast(i);
            codes[count] = if (cell.value == 0) ' ' else cell.value;
            count += 1;
        }
        for (names) |name| {
            var wide: [64]u32 = undefined;
            var length: usize = 0;
            var view = std.unicode.Utf8View.initUnchecked(name).iterator();
            while (view.nextCodepoint()) |c| : (length += 1) wide[length] = c;
            var start: usize = 0;
            while (start + length <= count) : (start += 1) {
                for (wide[0..length], codes[start..][0..length]) |a, b| {
                    if (a != b & ~linked_bit) break;
                } else for (codes[start..][0..length]) |*code| {
                    code.* |= linked_bit;
                }
            }
        }
        try links.push(std.testing.allocator, self, cells[0..count], codes[0..count], mode);
    }

    fn expect(self: *const Fake, links: *LineLinks, expected: []const [2]u32) !void {
        var out: std.AutoHashMapUnmanaged(u32, void) = .empty;
        defer out.deinit(std.testing.allocator);
        try links.place(std.testing.allocator, self, &out);
        var total: usize = 0;
        for (expected) |range| {
            for (range[0]..range[0] + range[1]) |cell| if (!out.contains(@intCast(cell))) return error.CellNotUnderlined;
            total += range[1];
        }
        try std.testing.expectEqual(total, out.count());
    }
};

const sample = "docs/foo.md";
/// Fills all but the last five cells of a row.
const long = "aaaaaaaaaaaaaaaaaaa";

fn resolved(screen: []const u8) !LineLinks {
    var links: LineLinks = .{};
    errdefer links.deinit(std.testing.allocator);
    try Fake.show(screen).resolve(&links, &.{sample}, 1);
    return links;
}

fn expectEdit(screen: []const u8, expected: []const [2]u32) !void {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    try Fake.show(screen).expect(&links, expected);
}

test "an edited line keeps a whole name wherever it moved" {
    try expectEdit("$ cat " ++ sample, &.{.{ 6, 11 }});
    try expectEdit("$ cat " ++ sample ++ " -n", &.{.{ 6, 11 }});
    try expectEdit("$ sudo cat " ++ sample ++ " -n", &.{.{ 11, 11 }});
    try expectEdit("$ cat " ++ sample ++ ".", &.{.{ 6, 11 }});
    try expectEdit("| > edit " ++ sample ++ "  |", &.{.{ 9, 11 }});
    try expectEdit("> \"" ++ sample ++ "\" " ++ sample, &.{ .{ 3, 11 }, .{ 16, 11 } });
}

test "a name stays through wrapping and scrolling" {
    // Wraps onto the next row.
    try expectEdit(long ++ " " ++ sample, &.{.{ 20, 11 }});
    // The line's first row is in scrollback.
    try expectEdit(long ++ " " ++ sample ++ "\n1\n2\n3", &.{.{ 0, 7 }});
}

test "a changed or removed name loses its underline" {
    try expectEdit("$ cat " ++ sample ++ "x", &.{});
    try expectEdit("$ cat x" ++ sample, &.{});
    try expectEdit("$ cat docs/fo.md", &.{});
    try expectEdit("$ cat ", &.{});
    try expectEdit("$ cat bar.md", &.{});
    try expectEdit("$ cat " ++ sample ++ ".bak", &.{});
}

test "a line still shown lends nothing" {
    try expectEdit("$ cat " ++ sample ++ "\nnew " ++ sample, &.{.{ 6, 11 }});
}

test "a line resolved without links stays without them" {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    const none = Fake.show("no " ++ sample);
    try none.resolve(&links, &.{}, 1);
    // The earlier resolution is the donor again, and its line is gone.
    try Fake.show("$ cat " ++ sample).resolve(&links, &.{sample}, 1);
    try none.expect(&links, &.{});
}

test "the next resolution replaces provisional underlines" {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    const line = Fake.show("$ ls " ++ sample);
    try line.expect(&links, &.{.{ 5, 11 }});
    // The host repeats the provisional underline while its resolution is pending.
    try line.resolve(&links, &.{sample}, 0);
    try line.expect(&links, &.{.{ 5, 11 }});
    try line.resolve(&links, &.{}, 1);
    try line.expect(&links, &.{});
    try Fake.show("$ ls -l " ++ sample).expect(&links, &.{});
}

test "a repeated push without links keeps the donors" {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    // A program cleared the line between two redraws.
    try Fake.show("").resolve(&links, &.{}, 0);
    try Fake.show("$ cat " ++ sample ++ " -n").expect(&links, &.{.{ 6, 11 }});
}

test "a line that changed since the host read it is left unknown" {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    const read = Fake.show("$ cat " ++ sample ++ " -n");
    const now = Fake.show("$ cat " ++ sample ++ " -nv");
    var cells: [24 * 4]u32 = undefined;
    var codes: [24 * 4]u32 = undefined;
    for (&cells, &codes, read.grid[0..cells.len], 0..) |*cell, *code, from, i| {
        cell.* = @intCast(i);
        code.* = if (from.value == 0) ' ' else from.value;
    }
    try links.push(std.testing.allocator, &now, &cells, &codes, 1);
    try now.expect(&links, &.{.{ 6, 11 }});
}

test "a name cut by the viewport's top is not lent" {
    var links = try resolved(long ++ " " ++ sample ++ "\n1\n2\n3");
    defer links.deinit(std.testing.allocator);
    // The viewport's part, "/foo.md", is all that was linked.
    try Fake.show(long ++ " " ++ sample ++ "\n1\n2\n3").resolve(&links, &.{"/foo.md"}, 1);
    try Fake.show("see /foo.md").expect(&links, &.{});
}

test "wide names carry across a wrap" {
    var links: LineLinks = .{};
    defer links.deinit(std.testing.allocator);
    try Fake.show("$ 文件.md").resolve(&links, &.{"文件.md"}, 1);
    try Fake.show("$ 文件.md -n").expect(&links, &.{.{ 2, 7 }});
    // The second wide character does not fit the first row, which ends in a spacer.
    try Fake.show(long ++ "a 文件.md").expect(&links, &.{ .{ 21, 2 }, .{ 24, 5 } });
}

test "dropping everything forgets lines and donors" {
    var links = try resolved("$ cat " ++ sample);
    defer links.deinit(std.testing.allocator);
    try links.push(std.testing.allocator, &Fake.show(""), &.{}, &.{}, 2);
    try Fake.show("$ cat " ++ sample).expect(&links, &.{});
    try Fake.show("$ cat " ++ sample ++ " -n").expect(&links, &.{});
}

test "remembered lines stay bounded" {
    var links: LineLinks = .{};
    defer links.deinit(std.testing.allocator);
    var text: [16]u8 = undefined;
    for (0..9000) |i| {
        try Fake.show(try std.fmt.bufPrint(&text, "line {d}", .{i})).resolve(&links, &.{}, 1);
        try std.testing.expect(links.known.count() <= 4096);
    }
}
