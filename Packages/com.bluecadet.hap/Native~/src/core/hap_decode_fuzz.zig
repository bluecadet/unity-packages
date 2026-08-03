//! hap_decode_fuzz.zig -- zig-native fuzz harness for
//! hap_decode.decodeTexture(), the per-frame Hap parser/decompressor the C
//! ABI's `hap_decode_texture` calls. Mirrors demuxer_fuzz.zig's Smith-based
//! pattern -- see that module's doc comment for the shared rationale (corpus
//! replay semantics, why coverage-guided `--fuzz` doesn't link for this
//! project, and the HAP_FUZZ_SECONDS bounded loop as the practical local
//! substitute). This file only covers what's different:
//!
//!  * The target is a single frame's bytes, not a whole MOV container, and
//!    the interesting bugs live deep inside section/table parsing -- raw
//!    random bytes almost never get past the first 4-byte header check
//!    (`readSectionHeader`'s size-vs-buffer bound). So on top of raw random
//!    bytes, this harness also seeds the loop with syntactically valid
//!    frames (built with the same buildRawFrame/createChunkedFrame builders
//!    the unit tests use, covering every supported format nibble, the
//!    Complex/chunked path, and a Multi-Image/HapM frame) and mutates them
//!    (byte flips, truncation, splices) so generated inputs still parse far
//!    enough to stress section-table bounds and chunk decode instructions.
//!  * Every texture the frame claims to carry is decoded by its own index,
//!    plus one index past the end -- the multi-texture (Hap Q Alpha) walk,
//!    which a single index-0 decode would leave unexercised.
//!  * A successful decode is repeated into a caller-owned, exactly-sized
//!    buffer the way `hap_decode_texture` does it (an ArrayListUnmanaged
//!    whose capacity *is* the caller's buffer, so the decode lands there
//!    with no allocation), and the two results must match byte for byte.
//!  * The invariants checked are: no crash, no leak (testing.allocator), no
//!    hang, no successful decode of an out-of-range texture index, and the
//!    zero-copy path agreeing with the allocating one. `error.InvalidFrame`
//!    and `error.OutOfMemory` are both expected, passing outcomes.
//!  * The existing tests/fixtures/fuzz_regressions/*.bin corpus (whole MOV
//!    files, not Hap frames) is replayed here too -- free coverage; they
//!    should all cleanly fail frame-level parsing with error.InvalidFrame.

const std = @import("std");
const testing = std.testing;

const hap_frame = @import("hap_frame.zig");
const hap_decode = @import("hap_decode.zig");
const test_support = @import("test_support.zig");

/// Cap on the byte buffer handed to the decoder per fuzz iteration. A
/// single Hap frame is far smaller than a whole MOV; 1 MiB is generous and
/// keeps iterations fast.
const max_input_len = 1 << 20; // 1 MiB

/// Texture indices probed per frame: the same cap the C ABI enforces. A
/// crafted Multi-Image frame can claim far more; decoding all of them would
/// only burn fuzz time.
const max_textures = hap_frame.max_textures;

/// Decode `smith`'s bytes as a Hap frame and check the contract. Anything
/// else (a crash, a leak caught by testing.allocator, or a mismatch here)
/// fails the fuzz run.
fn fuzzDecode(context: void, smith: *testing.Smith) !void {
    _ = context;

    var buf: [max_input_len]u8 = undefined;
    const len = smith.slice(&buf);

    const frame = buf[0..len];
    const count = hap_decode.frameTextureCount(frame) catch return;

    // One index past the last claimed texture (when the frame claims no more
    // than the ABI supports) must be rejected rather than decoded.
    const probe = @min(count, max_textures);
    var index: u32 = 0;
    while (index <= probe) : (index += 1) {
        var out = std.ArrayListUnmanaged(u8).empty;
        defer out.deinit(testing.allocator);

        _ = hap_decode.decodeTexture(testing.allocator, frame, index, &out) catch continue;

        if (index >= count) return error.DecodedOutOfRangeTexture;
        try expectCallerBufferDecodeMatches(frame, index, out.items);
    }
}

/// Re-decode texture `index` straight into a caller-owned buffer sized to
/// `expected`, the shape `hap_decode_texture` hands the decoder, and require
/// the same bytes as the allocating decode above.
///
/// No allocator wrapper is needed to make this safe: the buffer is exactly
/// the size this frame already decoded to, and decoding is a pure function
/// of the frame bytes, so the list never has to grow past the capacity it
/// starts with and the allocator is never asked to move it.
fn expectCallerBufferDecodeMatches(frame: []const u8, index: u32, expected: []const u8) !void {
    if (expected.len == 0) return;

    const dst = try testing.allocator.alloc(u8, expected.len);
    defer testing.allocator.free(dst);

    var out: std.ArrayListUnmanaged(u8) = .{ .items = dst[0..0], .capacity = dst.len };
    _ = try hap_decode.decodeTexture(testing.allocator, frame, index, &out);

    try testing.expect(out.items.ptr == dst.ptr);
    try testing.expectEqualSlices(u8, expected, out.items);
}

// -----------------------------------------------------------------------
// Structure-aware seed frames: syntactically valid Hap frames the mutation
// loop below starts from, so it penetrates past the top-level header check
// into section/table parsing.
// -----------------------------------------------------------------------

/// Build a handful of syntactically valid Hap frames covering: every
/// supported None-compressor format nibble, the two rejected BC6H (Hap HDR)
/// nibbles, Complex (chunked) frames with compressible chunk data (so some
/// chunks take the Snappy path), a Multi-Image (HapM) frame wrapping two
/// single-texture sub-sections, and an extended (8-byte) section header.
/// Caller frees each entry and the list itself via freeSeedFrames.
fn buildSeedFrames(allocator: std.mem.Allocator) !std.ArrayListUnmanaged([]u8) {
    var list = std.ArrayListUnmanaged([]u8).empty;
    errdefer freeSeedFrames(allocator, &list);

    const bc = [_]u8{0x11} ** 32;

    // None-compressor, one frame per supported format nibble.
    inline for (.{ 0xAB, 0xAE, 0xAF, 0xA1, 0xAC }) |type_byte| {
        try list.append(allocator, try test_support.buildRawFrame(allocator, &bc, type_byte));
    }

    // BC6H (Hap HDR): known-but-rejected nibbles, a distinct early-reject path.
    inline for (.{ 0xA2, 0xA3 }) |type_byte| {
        try list.append(allocator, try test_support.buildRawFrame(allocator, &bc, type_byte));
    }

    // Complex (chunked): compressible data so createChunkedFrame's own
    // per-chunk none-vs-snappy choice exercises both compressor paths.
    var compressible: [512]u8 = undefined;
    @memset(&compressible, 0x42);
    inline for (.{ 1, 2, 5 }) |chunk_count| {
        try list.append(allocator, try test_support.createChunkedFrame(allocator, &compressible, chunk_count, .rgb_dxt1));
    }

    // Multi-Image (HapM): two single-texture sub-sections back to back,
    // wrapped in a Multi-Image container.
    {
        const sub0 = try test_support.buildRawFrame(allocator, &bc, 0xAF); // None|YCoCg
        defer allocator.free(sub0);
        const sub1 = try test_support.buildRawFrame(allocator, &bc, 0xA1); // None|A_RGTC1
        defer allocator.free(sub1);

        try list.append(allocator, try test_support.buildMultiImage(allocator, &.{ sub0, sub1 }));
    }

    // Extended (8-byte) section header, zero-length payload -- forces the
    // 24-bit-size-zero branch in readSectionHeader.
    try list.append(allocator, try test_support.buildSectionExt(allocator, 0xAB, &.{}));

    return list;
}

fn freeSeedFrames(allocator: std.mem.Allocator, list: *std.ArrayListUnmanaged([]u8)) void {
    for (list.items) |f| allocator.free(f);
    list.deinit(allocator);
}

// -----------------------------------------------------------------------
// Smoke test: the existing MOV-file regression corpus (replayed here for
// free coverage -- they're whole containers, not Hap frames, so they should
// all cleanly return error.InvalidFrame) plus the synthetic seed frames
// above. Under a plain `zig build test` each corpus entry is replayed once
// (through Smith's length-prefix-quirked read -- see demuxer_fuzz.zig's doc
// comment) plus one implicit empty-input run.
// -----------------------------------------------------------------------

test "fuzz hap_decode.decodeTexture on arbitrary bytes" {
    var seeds = try buildSeedFrames(testing.allocator);
    defer freeSeedFrames(testing.allocator, &seeds);

    var corpus = std.ArrayListUnmanaged([]const u8).empty;
    defer corpus.deinit(testing.allocator);

    // The regression corpus is enumerated once in test_support, shared with
    // demuxer_fuzz.zig and fuzz_regressions_test.zig.
    var regressions = try test_support.RegressionCorpus.open(testing.allocator);
    defer regressions.deinit(testing.allocator);
    try corpus.appendSlice(testing.allocator, regressions.entries.items);

    for (seeds.items) |frame| try corpus.append(testing.allocator, frame);

    try testing.fuzz({}, fuzzDecode, .{ .corpus = corpus.items });
}

// -----------------------------------------------------------------------
// Opt-in, time-boxed random fuzz loop -- see demuxer_fuzz.zig's doc comment
// for why this "dumb" (uncoverage-guided) loop is the practical local
// substitute for `zig build test --fuzz` on this module. Each iteration
// picks one of two generation strategies: raw random bytes, or a mutated
// copy of one of the seed frames above (byte flip / truncate / splice).
// -----------------------------------------------------------------------

/// Cap on the random-bytes strategy's generated length -- large enough to
/// reach deep into a frame, small enough to keep iterations fast.
const random_bytes_cap = 1 << 17; // 128 KiB

fn fillRandomBytes(random: std.Random, buf: []u8) usize {
    const cap = @min(buf.len, random_bytes_cap);
    const len = random.intRangeAtMost(usize, 0, cap);
    random.bytes(buf[0..len]);
    return len;
}

/// Copy a random seed frame into `buf` and apply a small number of random
/// mutations (byte flip, truncation, or a short random splice), returning
/// the resulting length. Mutating real frames -- rather than only ever
/// generating fresh random bytes -- is what lets this loop reach
/// section-table and chunk-bounds logic that a valid header alone gates.
fn mutateSeed(random: std.Random, seeds: []const []const u8, buf: []u8) usize {
    if (seeds.len == 0) return 0;

    const seed = seeds[random.intRangeLessThan(usize, 0, seeds.len)];
    var len = @min(seed.len, buf.len);
    @memcpy(buf[0..len], seed[0..len]);

    const mutation_count = random.intRangeAtMost(u32, 1, 8);
    var i: u32 = 0;
    while (i < mutation_count) : (i += 1) {
        if (len == 0) break;
        switch (random.intRangeLessThan(u8, 0, 3)) {
            0 => { // flip a random byte
                const idx = random.intRangeLessThan(usize, 0, len);
                buf[idx] = random.int(u8);
            },
            1 => { // truncate to a random shorter (or equal) length
                len = random.intRangeAtMost(usize, 0, len);
            },
            2 => { // splice a few random bytes in at a random offset
                const splice_len = random.intRangeAtMost(usize, 1, 16);
                if (len + splice_len > buf.len) continue;
                const at = random.intRangeAtMost(usize, 0, len);
                std.mem.copyBackwards(u8, buf[at + splice_len ..][0 .. len - at], buf[at..len]);
                random.bytes(buf[at..][0..splice_len]);
                len += splice_len;
            },
            else => unreachable,
        }
    }
    return len;
}

test "bounded randomized fuzz (opt-in via HAP_FUZZ_SECONDS)" {
    const raw = std.c.getenv("HAP_FUZZ_SECONDS") orelse return error.SkipZigTest;
    const seconds = std.fmt.parseInt(u32, std.mem.span(raw), 10) catch return error.SkipZigTest;

    var seeds = try buildSeedFrames(testing.allocator);
    defer freeSeedFrames(testing.allocator, &seeds);

    var prng: std.Random.DefaultPrng = .init(testing.random_seed);
    const random = prng.random();

    const start_ms = test_support.nowMs();
    const deadline_ms = start_ms + @as(i64, seconds) * std.time.ms_per_s;

    var iterations: u64 = 0;
    var buf: [max_input_len]u8 = undefined;
    while (test_support.nowMs() < deadline_ms) : (iterations += 1) {
        const len = if (random.boolean())
            fillRandomBytes(random, &buf)
        else
            mutateSeed(random, seeds.items, &buf);

        var smith: testing.Smith = .{ .in = buf[0..len] };
        try fuzzDecode({}, &smith);
    }

    // stderr, for the reason demuxer_fuzz.zig spells out at its own summary.
    std.debug.print(
        "bounded randomized fuzz (decodeTexture): {d} iterations in {d}s\n",
        .{ iterations, seconds },
    );
}
