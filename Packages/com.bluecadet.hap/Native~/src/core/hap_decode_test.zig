//! hap_decode_test.zig — dedicated test suite for hap_decode.zig.
//!
//! Split out of hap_decode.zig per the project convention for large suites
//! (see demuxer_test.zig); core.zig's test block wires this file in
//! alongside the module itself. The synthetic frames come from
//! test_support.zig's builders, which the fuzz harnesses share.

const std = @import("std");
const testing = std.testing;

const hap_frame = @import("hap_frame.zig");
const hap_decode = @import("hap_decode.zig");
const test_support = @import("test_support.zig");

const HapTextureFormat = hap_frame.HapTextureFormat;

const readSectionHeader = hap_decode.readSectionHeader;
const frameTextureCount = hap_decode.frameTextureCount;
const frameTextureFormat = hap_decode.frameTextureFormat;
const frameTextureChunkCount = hap_decode.frameTextureChunkCount;
const decodeTexture = hap_decode.decodeTexture;

test "readSectionHeader parses a 4-byte header" {
    const buf = [_]u8{ 0x03, 0x00, 0x00, 0xAB, 0x11, 0x22, 0x33 };
    const h = try readSectionHeader(&buf);
    try testing.expectEqual(@as(usize, 4), h.header_len);
    try testing.expectEqual(@as(usize, 3), h.size);
    try testing.expectEqual(@as(u8, 0xAB), h.type);
}

test "readSectionHeader parses an extended 8-byte header" {
    // 24-bit size zero -> real size in bytes 4..7 (LE). type at byte 3.
    var buf = [_]u8{0} ** 12;
    buf[3] = 0xAB;
    std.mem.writeInt(u32, buf[4..8], 4, .little);
    const h = try readSectionHeader(&buf);
    try testing.expectEqual(@as(usize, 8), h.header_len);
    try testing.expectEqual(@as(usize, 4), h.size);
    try testing.expectEqual(@as(u8, 0xAB), h.type);
}

test "readSectionHeader rejects a truncated 4-byte header" {
    const buf = [_]u8{ 0x01, 0x00, 0x00 };
    try testing.expectError(error.InvalidFrame, readSectionHeader(&buf));
}

test "readSectionHeader rejects a truncated extended header" {
    // 24-bit size zero selects the 8-byte form, but only 5 bytes present.
    const buf = [_]u8{ 0x00, 0x00, 0x00, 0xAB, 0x00 };
    try testing.expectError(error.InvalidFrame, readSectionHeader(&buf));
}

test "readSectionHeader rejects a size that overruns the buffer" {
    // Declares 16 payload bytes but only 4 follow.
    const buf = [_]u8{ 0x10, 0x00, 0x00, 0xAB, 0, 0, 0, 0 };
    try testing.expectError(error.InvalidFrame, readSectionHeader(&buf));
}

test "frameTextureCount returns 1 for a single-texture frame" {
    const payload = [_]u8{0} ** 8;
    const frame = try test_support.buildSection(testing.allocator, 0xAB, &payload);
    defer testing.allocator.free(frame);
    try testing.expectEqual(@as(u32, 1), try frameTextureCount(frame));
}

test "frameTextureCount walks multi-image sub-sections" {
    const block = [_]u8{0} ** 8;
    const sub = try test_support.buildSection(testing.allocator, 0xAB, &block);
    defer testing.allocator.free(sub);

    const frame = try test_support.buildMultiImage(testing.allocator, &.{ sub, sub });
    defer testing.allocator.free(frame);

    try testing.expectEqual(@as(u32, 2), try frameTextureCount(frame));
}

test "frameTextureFormat maps the format nibble" {
    const payload = [_]u8{0} ** 8;
    const frame = try test_support.buildSection(testing.allocator, 0xAF, &payload); // None|YCoCg
    defer testing.allocator.free(frame);
    try testing.expectEqual(HapTextureFormat.ycocg_dxt5, try frameTextureFormat(frame, 0));
}

test "decodeTexture rejects BC6H (Hap HDR) format nibbles" {
    const payload = [_]u8{0} ** 8;
    inline for (.{ 0xA2, 0xA3 }) |type_byte| { // None|BC6H-unsigned / -signed
        const frame = try test_support.buildSection(testing.allocator, type_byte, &payload);
        defer testing.allocator.free(frame);
        var out = std.ArrayListUnmanaged(u8).empty;
        defer out.deinit(testing.allocator);
        try testing.expectError(error.InvalidFrame, decodeTexture(testing.allocator, frame, 0, &out));
    }
}

test "decodeTexture copies a None-compressor texture verbatim" {
    const bc = [_]u8{ 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
    const frame = try test_support.buildSection(testing.allocator, 0xAB, &bc);
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    const fmt = try decodeTexture(testing.allocator, frame, 0, &out);
    try testing.expectEqual(HapTextureFormat.rgb_dxt1, fmt);
    try testing.expectEqualSlices(u8, &bc, out.items);
}

test "decodeTexture decodes a multi-chunk None Complex frame (offset table absent)" {
    const c0 = [_]u8{ 0xAA, 0xBB, 0xCC, 0xDD };
    const c1 = [_]u8{ 0x11, 0x22 };
    const c2 = [_]u8{ 0x77, 0x88, 0x99 };
    const frame = try test_support.buildComplex(testing.allocator, &.{ &c0, &c1, &c2 }, .{});
    defer testing.allocator.free(frame);

    try testing.expectEqual(@as(u32, 3), try frameTextureChunkCount(frame, 0));

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    const fmt = try decodeTexture(testing.allocator, frame, 0, &out);
    try testing.expectEqual(HapTextureFormat.rgb_dxt1, fmt);
    try testing.expectEqualSlices(u8, &(c0 ++ c1 ++ c2), out.items);
}

test "decodeTexture decodes a Complex frame with an explicit offset table" {
    const c0 = [_]u8{ 0xDE, 0xAD };
    const c1 = [_]u8{ 0xBE, 0xEF, 0x00 };
    const frame = try test_support.buildComplex(
        testing.allocator,
        &.{ &c0, &c1 },
        .{ .with_offsets = true },
    );
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    _ = try decodeTexture(testing.allocator, frame, 0, &out);
    try testing.expectEqualSlices(u8, &(c0 ++ c1), out.items);
}

test "parseComplexInstructions skips unknown sub-sections" {
    const c0 = [_]u8{ 0x01, 0x02 };

    // An unknown (type 0x7F) sub-section spliced into the container before
    // the required tables must be stepped over, not tripped on.
    const unknown = try test_support.buildSection(testing.allocator, 0x7F, &[_]u8{ 0xFF, 0xFF });
    defer testing.allocator.free(unknown);

    const frame = try test_support.buildComplex(
        testing.allocator,
        &.{&c0},
        .{ .extra_sections = &.{unknown} },
    );
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    _ = try decodeTexture(testing.allocator, frame, 0, &out);
    try testing.expectEqualSlices(u8, &c0, out.items);
}

test "parseComplexInstructions rejects mismatched table chunk counts" {
    // Compressor table says 2 chunks, size table says 1.
    const c0 = [_]u8{ 0x01, 0x02, 0x03, 0x04 };
    const frame = try test_support.buildComplex(
        testing.allocator,
        &.{&c0},
        .{ .compressors = &.{ test_support.compressor_none, test_support.compressor_none } },
    );
    defer testing.allocator.free(frame);

    try testing.expectError(error.InvalidFrame, frameTextureChunkCount(frame, 0));
}

test "decodeTexture rejects a chunk with a bad compressor byte" {
    // 0xC is a valid *section* compressor (Complex) but never a chunk's.
    const c0 = [_]u8{ 0x01, 0x02 };
    const frame = try test_support.buildComplex(
        testing.allocator,
        &.{&c0},
        .{ .compressors = &.{test_support.compressor_complex} },
    );
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    try testing.expectError(error.InvalidFrame, decodeTexture(testing.allocator, frame, 0, &out));
}

test "decodeTexture rejects a Complex frame with zero chunks" {
    // Empty compressor and size tables -> chunk count 0.
    const frame = try test_support.buildComplex(testing.allocator, &.{}, .{});
    defer testing.allocator.free(frame);

    // The query reports 0; decode rejects it.
    try testing.expectEqual(@as(u32, 0), try frameTextureChunkCount(frame, 0));

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    try testing.expectError(error.InvalidFrame, decodeTexture(testing.allocator, frame, 0, &out));
}

test "decodeComplex bounds-checks a chunk size against the frame data" {
    // Size table claims a 16-byte chunk but only 2 frame-data bytes follow.
    const c0 = [_]u8{ 0x01, 0x02 };
    const frame = try test_support.buildComplex(
        testing.allocator,
        &.{&c0},
        .{ .declared_sizes = &.{16} },
    );
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    try testing.expectError(error.InvalidFrame, decodeTexture(testing.allocator, frame, 0, &out));
}

// -----------------------------------------------------------------------
// Multi-texture (Hap Q Alpha) decode.
//
// A Multi-Image frame carries one section per texture, and each must be
// decoded with *its own* index -- the reference Unity plugin hardcoded
// index 0 and so decoded the color texture twice.
// -----------------------------------------------------------------------

test "decodeTexture decodes each texture of a multi-image frame from its own index" {
    // Two valid Hap1 (0xAB) sub-images with different content: a decode that
    // read the wrong index -- or aliased the two output buffers -- shows up
    // as one texture's bytes appearing in place of the other's.
    const block0 = [8]u8{ 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 };
    const block1 = [8]u8{ 0x00, 0xF8, 0x1F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };

    const sub0 = try test_support.buildRawFrame(testing.allocator, &block0, 0xAB);
    defer testing.allocator.free(sub0);
    const sub1 = try test_support.buildRawFrame(testing.allocator, &block1, 0xAF); // None|YCoCg
    defer testing.allocator.free(sub1);

    const frame = try test_support.buildMultiImage(testing.allocator, &.{ sub0, sub1 });
    defer testing.allocator.free(frame);

    try testing.expectEqual(@as(u32, 2), try frameTextureCount(frame));

    var out0 = std.ArrayListUnmanaged(u8).empty;
    defer out0.deinit(testing.allocator);
    var out1 = std.ArrayListUnmanaged(u8).empty;
    defer out1.deinit(testing.allocator);

    try testing.expectEqual(HapTextureFormat.rgb_dxt1, try decodeTexture(testing.allocator, frame, 0, &out0));
    try testing.expectEqual(HapTextureFormat.ycocg_dxt5, try decodeTexture(testing.allocator, frame, 1, &out1));

    try testing.expectEqualSlices(u8, &block0, out0.items);
    try testing.expectEqualSlices(u8, &block1, out1.items);
}

test "decodeTexture rejects a texture index past the end of a multi-image frame" {
    const block = [_]u8{0x11} ** 8;
    const sub = try test_support.buildRawFrame(testing.allocator, &block, 0xAB);
    defer testing.allocator.free(sub);

    const frame = try test_support.buildMultiImage(testing.allocator, &.{sub});
    defer testing.allocator.free(frame);

    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);
    try testing.expectError(error.InvalidFrame, decodeTexture(testing.allocator, frame, 1, &out));
}

// -----------------------------------------------------------------------
// Chunking is transport: a Complex frame must decode to exactly the bytes
// the same texture data produces when it isn't chunked.
// -----------------------------------------------------------------------

test "decodeTexture output of a Snappy-chunked frame is byte-identical to unchunked" {
    // 64x32 pixels = 128 BC1 blocks (1024 bytes) of repeating content, so
    // createChunkedFrame's per-chunk Snappy compression actually wins and
    // the chunks take the Snappy path rather than falling back to None.
    var bc1_blocks: [1024]u8 = undefined;
    var i: usize = 0;
    while (i < bc1_blocks.len) : (i += 8) {
        bc1_blocks[i + 0] = 0xFF;
        bc1_blocks[i + 1] = 0xFF; // color0: white
        bc1_blocks[i + 2] = 0x00;
        bc1_blocks[i + 3] = 0x00; // color1: black
        bc1_blocks[i + 4] = 0x00;
        bc1_blocks[i + 5] = 0x00; // indices: all 0
        bc1_blocks[i + 6] = 0x00;
        bc1_blocks[i + 7] = 0x00;
    }

    const unchunked = try test_support.buildRawFrame(testing.allocator, &bc1_blocks, 0xAB);
    defer testing.allocator.free(unchunked);

    const chunked = try test_support.createChunkedFrame(testing.allocator, &bc1_blocks, 4, .rgb_dxt1);
    defer testing.allocator.free(chunked);

    // Complex|DXT1, split the way the builder was asked to split it.
    try testing.expectEqual(@as(u8, 0xCB), chunked[3]);
    try testing.expectEqual(@as(u32, 4), try frameTextureChunkCount(chunked, 0));

    var out_unchunked = std.ArrayListUnmanaged(u8).empty;
    defer out_unchunked.deinit(testing.allocator);
    var out_chunked = std.ArrayListUnmanaged(u8).empty;
    defer out_chunked.deinit(testing.allocator);

    try testing.expectEqual(
        HapTextureFormat.rgb_dxt1,
        try decodeTexture(testing.allocator, unchunked, 0, &out_unchunked),
    );
    try testing.expectEqual(
        HapTextureFormat.rgb_dxt1,
        try decodeTexture(testing.allocator, chunked, 0, &out_chunked),
    );

    try testing.expectEqualSlices(u8, &bc1_blocks, out_unchunked.items);
    try testing.expectEqualSlices(u8, out_unchunked.items, out_chunked.items);
}

// -----------------------------------------------------------------------
// Fixture-backed decode: demux a real .mov and decode frame 0 through the
// same call the C ABI makes. A missing fixture skips rather than fails (see
// test_support.openFixture).
// -----------------------------------------------------------------------

const fixture_hap1 = "tests/fixtures/hap1.mov";
const fixture_hap5 = "tests/fixtures/hap5.mov";
const fixture_hap7 = "tests/fixtures/hap7.mov";
const fixture_hapy = "tests/fixtures/hapy.mov";
const fixture_hapm = "tests/fixtures/hapm.mov";

/// Demux `path` and decode texture `index` of frame 0 into `out`, returning
/// its format. Propagates `error.SkipZigTest` for a missing fixture, so a
/// per-case loop can skip just that case.
fn decodeFixtureTexture(
    path: []const u8,
    index: u32,
    out: *std.ArrayListUnmanaged(u8),
) !HapTextureFormat {
    var fixture = try test_support.openFixture(path);
    defer fixture.deinit();

    try testing.expect(fixture.demuxer.track.frame_count > 0);
    return decodeTexture(testing.allocator, try fixture.sample(0), index, out);
}

test "decodeTexture per-codec fixtures decode to the expected format and byte count" {
    const Case = struct {
        path: []const u8,
        format: HapTextureFormat,
        bytes: usize, // 640x360 at the format's BC block size
    };
    const cases = [_]Case{
        .{ .path = fixture_hap1, .format = .rgb_dxt1, .bytes = 115200 },
        .{ .path = fixture_hap5, .format = .rgba_dxt5, .bytes = 230400 },
        .{ .path = fixture_hap7, .format = .rgba_bptc_unorm, .bytes = 230400 },
        .{ .path = fixture_hapy, .format = .ycocg_dxt5, .bytes = 230400 },
    };

    for (cases) |c| {
        var out = std.ArrayListUnmanaged(u8).empty;
        defer out.deinit(testing.allocator);

        const format = decodeFixtureTexture(c.path, 0, &out) catch |err| switch (err) {
            error.SkipZigTest => continue, // fixture missing: skip this case
            else => return err,
        };

        try testing.expectEqual(c.format, format);
        try testing.expectEqual(c.bytes, out.items.len);
    }
}

test "decodeTexture chunked fixtures decode byte-identical to their unchunked counterparts" {
    const Case = struct {
        unchunked: []const u8,
        chunked: []const u8,
    };
    const cases = [_]Case{
        .{ .unchunked = fixture_hap1, .chunked = "tests/fixtures/hap1_chunked.mov" },
        .{ .unchunked = fixture_hap5, .chunked = "tests/fixtures/hap5_chunked.mov" },
        .{ .unchunked = fixture_hapy, .chunked = "tests/fixtures/hapy_chunked.mov" },
    };

    for (cases) |c| {
        var unchunked = std.ArrayListUnmanaged(u8).empty;
        defer unchunked.deinit(testing.allocator);
        var chunked = std.ArrayListUnmanaged(u8).empty;
        defer chunked.deinit(testing.allocator);

        _ = decodeFixtureTexture(c.unchunked, 0, &unchunked) catch |err| switch (err) {
            error.SkipZigTest => continue, // fixture pair missing: skip this case
            else => return err,
        };
        _ = try decodeFixtureTexture(c.chunked, 0, &chunked);

        try testing.expectEqualSlices(u8, unchunked.items, chunked.items);
    }
}

test "decodeTexture Hap1 fixture frame0 matches the committed golden texture" {
    var out = std.ArrayListUnmanaged(u8).empty;
    defer out.deinit(testing.allocator);

    _ = try decodeFixtureTexture(fixture_hap1, 0, &out);

    try test_support.expectMatchesGolden("tests/fixtures/hap1_golden.bin", out.items);
}

test "decodeTexture Hap Q Alpha fixture frame0 decodes both textures against their goldens" {
    // tests/fixtures/hapm.mov is a real Multi-Image (0x0D) frame wrapping a
    // YCoCg DXT5 section and an A_RGTC1 section (ffmpeg cannot encode HapM;
    // see tests/fixtures/README.md), so this pins the two-texture path
    // against an on-disk sample rather than only a synthetic frame.
    var color = std.ArrayListUnmanaged(u8).empty;
    defer color.deinit(testing.allocator);
    var alpha = std.ArrayListUnmanaged(u8).empty;
    defer alpha.deinit(testing.allocator);

    const color_format = try decodeFixtureTexture(fixture_hapm, 0, &color);
    const alpha_format = try decodeFixtureTexture(fixture_hapm, 1, &alpha);

    try testing.expectEqual(HapTextureFormat.ycocg_dxt5, color_format);
    try testing.expectEqual(HapTextureFormat.a_rgtc1, alpha_format);

    try test_support.expectMatchesGolden("tests/fixtures/hapm_golden_tex0.bin", color.items);
    try test_support.expectMatchesGolden("tests/fixtures/hapm_golden_tex1.bin", alpha.items);
}
