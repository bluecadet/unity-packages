//! test_support.zig — shared scaffolding for the test and fuzz files:
//! a timing helper, synthetic Hap frame builders, fixture/golden helpers,
//! and the fuzz regression corpus (demuxer_test.zig, hap_decode_test.zig,
//! bluecadet_hap_test.zig, fuzz_regressions_test.zig, and the two fuzz
//! harnesses). Not a test file itself (no `test` blocks), so it is not
//! referenced from core.zig's aggregate test block -- it's imported directly
//! by the files that need it.
//!
//! Zig 0.16 note: the clock reading and directory iteration below go through
//! `std.Io.Clock`/`std.Io.Dir`, which need an `Io` instance; the
//! single-threaded global one is fine here because only test code on one
//! thread ever uses it (see sync.zig for why production code cannot use that
//! instance).

const std = @import("std");
const testing = std.testing;

const hap_frame = @import("hap_frame.zig");
const mmap_reader = @import("mmap_reader.zig");
const demuxer_mod = @import("demuxer.zig");

const MmapReader = mmap_reader.MmapReader;
const Demuxer = demuxer_mod.Demuxer;

pub fn io() std.Io {
    return std.Io.Threaded.global_single_threaded.io();
}

/// Monotonic clock reading, in milliseconds.
pub fn nowMs() i64 {
    return std.Io.Clock.awake.now(io()).toMilliseconds();
}

// -----------------------------------------------------------------------
// Synthetic Hap frame builders.
//
// A Hap frame structure:
//   4-byte header: length(3 bytes LE) + type(1 byte)
//   For single-chunk None compressor:
//     type byte = 0xAB (Hap1), 0xAE (Hap5), 0xAC (Hap7)
//   Frame data = raw BC block bytes (pass-through for None compressor)
// -----------------------------------------------------------------------

// Snappy C API (hand-declared, no @cImport per project convention; mirrors
// the extern style hap_decode.zig uses for snappy_uncompress). Needed only
// by createChunkedFrame below, to compress each chunk it builds.
const snappy_ok: c_int = 0;

extern fn snappy_compress(
    input: [*]const u8,
    input_length: usize,
    compressed: [*]u8,
    compressed_length: *usize,
) c_int;

extern fn snappy_max_compressed_length(source_length: usize) usize;

// Wire-format constants (from the Hap spec; mirrors hap_decode.zig's
// private copies -- these are test-only, so duplicating a handful of `u8`
// constants beats making hap_decode.zig export them for a single caller).
// The compressor bytes are public because the builders take them as
// arguments, so their callers have to name them too.
pub const compressor_none: u8 = 0xA;
pub const compressor_snappy: u8 = 0xB;
pub const compressor_complex: u8 = 0xC;

const section_multi_image: u8 = 0x0D;
const section_decode_instructions: u8 = 0x01;
const section_chunk_compressor_table: u8 = 0x02;
const section_chunk_size_table: u8 = 0x03;
const section_chunk_offset_table: u8 = 0x04;

/// Build a synthetic Hap frame with a given type byte (None compressor,
/// single chunk): the 4-byte header wraps `bc_data` unmodified.
pub fn buildRawFrame(allocator: std.mem.Allocator, bc_data: []const u8, type_byte: u8) ![]u8 {
    return buildSection(allocator, type_byte, bc_data);
}

/// Map a HapTextureFormat to its section-type-byte format nibble (the
/// inverse of hap_decode.zig's private textureFormatFromNibble).
fn formatNibble(format: hap_frame.HapTextureFormat) u8 {
    return switch (format) {
        .rgb_dxt1 => 0xB,
        .rgba_dxt5 => 0xE,
        .ycocg_dxt5 => 0xF,
        .a_rgtc1 => 0x1,
        .rgba_bptc_unorm => 0xC,
    };
}

/// Build a [size24][type] section header wrapping `payload`, using the short
/// 4-byte header form. Shared by everything that hand-builds Hap wire bytes
/// (hap_decode_test.zig, hap_decode_fuzz.zig's seed frames).
pub fn buildSection(allocator: std.mem.Allocator, type_byte: u8, payload: []const u8) ![]u8 {
    const out = try allocator.alloc(u8, 4 + payload.len);
    const len: u32 = @intCast(payload.len);
    out[0] = @truncate(len);
    out[1] = @truncate(len >> 8);
    out[2] = @truncate(len >> 16);
    out[3] = type_byte;
    @memcpy(out[4..], payload);
    return out;
}

/// Build a section using the extended 8-byte header form (24-bit size zero,
/// real size in bytes 4..7). The only way to encode a zero-length section,
/// since the short form's zero size selects this extended form.
pub fn buildSectionExt(allocator: std.mem.Allocator, type_byte: u8, payload: []const u8) ![]u8 {
    const out = try allocator.alloc(u8, 8 + payload.len);
    out[0] = 0;
    out[1] = 0;
    out[2] = 0;
    out[3] = type_byte;
    std.mem.writeInt(u32, out[4..8], @intCast(payload.len), .little);
    @memcpy(out[8..], payload);
    return out;
}

/// Build a section, picking the header form the payload requires: a
/// zero-length payload can only be spelled with the extended form.
fn buildSectionAuto(allocator: std.mem.Allocator, type_byte: u8, payload: []const u8) ![]u8 {
    return if (payload.len == 0)
        buildSectionExt(allocator, type_byte, payload)
    else
        buildSection(allocator, type_byte, payload);
}

/// Wrap already-encoded sections in a Multi-Image (0x0D) top-level section:
/// the on-disk shape a Hap Q Alpha frame has, and what hap_decode.zig walks
/// to reach texture `index`.
pub fn buildMultiImage(allocator: std.mem.Allocator, sections: []const []const u8) ![]u8 {
    var body = std.ArrayListUnmanaged(u8).empty;
    defer body.deinit(allocator);
    for (sections) |s| try body.appendSlice(allocator, s);
    return buildSection(allocator, section_multi_image, body.items);
}

/// Knobs for `buildComplex`. The defaults build the frame a well-formed
/// Complex texture has: one None-compressor chunk per entry, a compressor
/// and size table, and no offset table. Every other field exists so a test
/// can build one specific kind of malformed (or unusual) frame.
pub const ComplexOptions = struct {
    format: hap_frame.HapTextureFormat = .rgb_dxt1,
    /// Compressor byte per chunk table entry; defaults to `compressor_none`
    /// for every chunk. Chunk payloads are stored verbatim, so a caller that
    /// declares Snappy must pass already-compressed bytes. A list whose
    /// length differs from the chunk count builds a frame whose tables
    /// disagree about how many chunks there are.
    compressors: ?[]const u8 = null,
    /// Sizes written into the size table, when they must differ from the
    /// chunks' real lengths (defaults to the real lengths).
    declared_sizes: ?[]const u32 = null,
    /// Emit an offset table holding the cumulative chunk offsets. Absent,
    /// the decoder derives the same offsets itself.
    with_offsets: bool = false,
    /// Sub-sections spliced into the decode-instructions container ahead of
    /// the tables (e.g. an unknown type the parser must skip).
    extra_sections: []const []const u8 = &.{},
};

/// Build a Complex (chunked) single-texture Hap frame carrying `chunks`
/// back to back as its frame data. See `ComplexOptions` for the ways the
/// emitted tables can be bent away from the well-formed shape.
pub fn buildComplex(
    allocator: std.mem.Allocator,
    chunks: []const []const u8,
    options: ComplexOptions,
) ![]u8 {
    const n = chunks.len;

    const compressor_tbl = try allocator.alloc(u8, if (options.compressors) |c| c.len else n);
    defer allocator.free(compressor_tbl);
    if (options.compressors) |c| @memcpy(compressor_tbl, c) else @memset(compressor_tbl, compressor_none);

    const size_tbl = try allocator.alloc(u8, n * 4);
    defer allocator.free(size_tbl);
    const offset_tbl = try allocator.alloc(u8, n * 4);
    defer allocator.free(offset_tbl);

    var frame_data = std.ArrayListUnmanaged(u8).empty;
    defer frame_data.deinit(allocator);

    var running: u32 = 0;
    for (chunks, 0..) |chunk, i| {
        const declared: u32 = if (options.declared_sizes) |s| s[i] else @intCast(chunk.len);
        std.mem.writeInt(u32, size_tbl[i * 4 ..][0..4], declared, .little);
        std.mem.writeInt(u32, offset_tbl[i * 4 ..][0..4], running, .little);
        running += declared;
        try frame_data.appendSlice(allocator, chunk);
    }

    const sec_comp = try buildSectionAuto(allocator, section_chunk_compressor_table, compressor_tbl);
    defer allocator.free(sec_comp);
    const sec_size = try buildSectionAuto(allocator, section_chunk_size_table, size_tbl);
    defer allocator.free(sec_size);
    const sec_off = try buildSectionAuto(allocator, section_chunk_offset_table, offset_tbl);
    defer allocator.free(sec_off);

    var container_body = std.ArrayListUnmanaged(u8).empty;
    defer container_body.deinit(allocator);
    for (options.extra_sections) |s| try container_body.appendSlice(allocator, s);
    try container_body.appendSlice(allocator, sec_comp);
    try container_body.appendSlice(allocator, sec_size);
    if (options.with_offsets) try container_body.appendSlice(allocator, sec_off);

    const container = try buildSection(allocator, section_decode_instructions, container_body.items);
    defer allocator.free(container);

    var payload = std.ArrayListUnmanaged(u8).empty;
    defer payload.deinit(allocator);
    try payload.appendSlice(allocator, container);
    try payload.appendSlice(allocator, frame_data.items);

    const type_byte = (compressor_complex << 4) | formatNibble(options.format);
    return buildSection(allocator, type_byte, payload.items);
}

/// Build a synthetic Complex (chunked) Hap frame carrying `tex_data`, split
/// into exactly `chunk_count` roughly-equal contiguous chunks (clamped to a
/// minimum of 1). Each chunk is Snappy-compressed; a chunk whose compressed
/// form isn't smaller than the original is instead stored uncompressed.
///
/// Unlike the old HapEncode-backed version, the chunk count here never
/// collapses: the caller always gets back exactly `chunk_count` chunks,
/// even over incompressible or tiny input.
pub fn createChunkedFrame(
    allocator: std.mem.Allocator,
    tex_data: []const u8,
    chunk_count: u32,
    format: hap_frame.HapTextureFormat,
) ![]u8 {
    const n = @max(chunk_count, 1);

    const compressors = try allocator.alloc(u8, n);
    defer allocator.free(compressors);

    // One owned payload per chunk (compressed or a verbatim copy), so the
    // slices handed to buildComplex all outlive the loop that fills them.
    var payloads = std.ArrayListUnmanaged([]const u8).empty;
    defer {
        for (payloads.items) |p| allocator.free(p);
        payloads.deinit(allocator);
    }

    const base = tex_data.len / n;
    const remainder = tex_data.len % n;

    var start: usize = 0;
    for (0..n) |i| {
        const extra: usize = if (i < remainder) 1 else 0;
        const end = start + base + extra;
        const chunk = tex_data[start..end];
        start = end;

        const scratch = try allocator.alloc(u8, snappy_max_compressed_length(chunk.len));
        defer allocator.free(scratch);

        var out_len: usize = scratch.len;
        const status = snappy_compress(chunk.ptr, chunk.len, scratch.ptr, &out_len);

        const compressed = status == snappy_ok and out_len < chunk.len;
        compressors[i] = if (compressed) compressor_snappy else compressor_none;
        try payloads.append(allocator, try allocator.dupe(u8, if (compressed) scratch[0..out_len] else chunk));
    }

    return buildComplex(allocator, payloads.items, .{
        .format = format,
        .compressors = compressors,
    });
}

// -----------------------------------------------------------------------
// Fixture helpers.
//
// Fixture paths are relative to Native~/, which is where `zig build test`
// sets the test runner's working directory (see build.zig's
// `run_tests.setCwd`). A missing fixture skips the test rather than failing
// it, so a stripped checkout still runs the synthetic suites.
// -----------------------------------------------------------------------

/// An open fixture: the mapped file plus the demuxer that parsed it.
pub const Fixture = struct {
    reader: MmapReader,
    demuxer: Demuxer,

    pub fn deinit(self: *Fixture) void {
        self.demuxer.deinit(testing.allocator);
        self.reader.deinit();
    }

    /// Compressed sample bytes of frame `index`.
    pub fn sample(self: *const Fixture, index: u32) ![]const u8 {
        return self.demuxer.sampleData(&self.reader, index) orelse error.TestUnexpectedResult;
    }
};

/// Map and demux the fixture at `path`. A missing file yields
/// `error.SkipZigTest`, which a whole test can propagate and a per-case loop
/// can catch to skip just that case.
pub fn openFixture(path: []const u8) !Fixture {
    var reader = MmapReader.init(path) catch |err| switch (err) {
        error.OpenFailed => return error.SkipZigTest,
        else => return err,
    };
    errdefer reader.deinit();

    var dem: Demuxer = .{};
    errdefer dem.deinit(testing.allocator);
    try dem.open(testing.allocator, &reader);

    return .{ .reader = reader, .demuxer = dem };
}

/// Assert `actual` equals the committed golden dump at `path`, skipping if
/// the golden isn't present.
pub fn expectMatchesGolden(path: []const u8, actual: []const u8) !void {
    var golden = MmapReader.init(path) catch |err| switch (err) {
        error.OpenFailed => return error.SkipZigTest,
        else => return err,
    };
    defer golden.deinit();

    try testing.expectEqual(golden.data.len, actual.len);
    try testing.expectEqualSlices(u8, golden.data, actual);
}

// -----------------------------------------------------------------------
// Fuzz regression corpus.
//
// The fixtures under tests/fixtures/fuzz_regressions/ are enumerated here,
// once, by walking the directory: fuzz_regressions_test.zig replays them and
// both fuzz harnesses seed their corpus with them, and a hardcoded path list
// in each would silently stop covering newly added fixtures.
// -----------------------------------------------------------------------

pub const regression_dir = "tests/fixtures/fuzz_regressions";

/// Every file under `regression_dir`, mapped read-only for the lifetime of
/// the corpus.
pub const RegressionCorpus = struct {
    /// True when the corpus directory itself exists -- distinct from a
    /// present-but-empty directory, which is a broken checkout rather than a
    /// stripped one.
    present: bool = false,
    readers: std.ArrayListUnmanaged(MmapReader) = .empty,
    /// The mapped bytes, one entry per file, in directory order.
    entries: std.ArrayListUnmanaged([]const u8) = .empty,

    pub fn open(allocator: std.mem.Allocator) !RegressionCorpus {
        var corpus: RegressionCorpus = .{};
        errdefer corpus.deinit(allocator);

        var dir = std.Io.Dir.cwd().openDir(io(), regression_dir, .{ .iterate = true }) catch
            return corpus; // no fixtures in this checkout
        defer dir.close(io());
        corpus.present = true;

        var it = dir.iterate();
        while (try it.next(io())) |entry| {
            if (entry.kind != .file) continue;

            var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
            const path = try std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ regression_dir, entry.name });

            const reader = MmapReader.init(path) catch continue;
            try corpus.readers.append(allocator, reader);
            try corpus.entries.append(allocator, corpus.readers.items[corpus.readers.items.len - 1].data);
        }

        return corpus;
    }

    pub fn deinit(self: *RegressionCorpus, allocator: std.mem.Allocator) void {
        for (self.readers.items) |*r| r.deinit();
        self.readers.deinit(allocator);
        self.entries.deinit(allocator);
        self.* = .{};
    }
};
