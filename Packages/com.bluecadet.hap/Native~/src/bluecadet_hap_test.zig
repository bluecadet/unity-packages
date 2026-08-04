//! bluecadet_hap_test.zig -- end-to-end tests of the exported C ABI
//! (src/bluecadet_hap.zig) against the committed .mov fixtures: open,
//! metadata, decode of every frame of every Hap variant, Hap Q Alpha's two
//! textures, and the error codes for bad paths, bad files and bad arguments.
//!
//! Fixture paths are relative to Native~/, which is where `zig build test`
//! sets the test runner's working directory.

const std = @import("std");
const testing = std.testing;

const abi = @import("bluecadet_hap.zig");
const test_support = @import("core/test_support.zig");

const HapError = abi.HapError;
const Handle = abi.Handle;

fn ok(err: HapError) i32 {
    return @intFromEnum(err);
}

/// Format codes from bluecadet_hap.h.
const fmt_dxt1: i32 = 1;
const fmt_dxt5: i32 = 2;
const fmt_bc7: i32 = 3;
const fmt_ycocg_dxt5: i32 = 4;
const fmt_rgtc1: i32 = 5;

const Fixture = struct {
    path: [:0]const u8,
    texture_count: i32,
    formats: []const i32,
};

const fixtures = [_]Fixture{
    .{ .path = "tests/fixtures/hap1.mov", .texture_count = 1, .formats = &.{fmt_dxt1} },
    .{ .path = "tests/fixtures/hap1_chunked.mov", .texture_count = 1, .formats = &.{fmt_dxt1} },
    .{ .path = "tests/fixtures/hap1_audio.mov", .texture_count = 1, .formats = &.{fmt_dxt1} },
    .{ .path = "tests/fixtures/hap5.mov", .texture_count = 1, .formats = &.{fmt_dxt5} },
    .{ .path = "tests/fixtures/hap5_chunked.mov", .texture_count = 1, .formats = &.{fmt_dxt5} },
    .{ .path = "tests/fixtures/hapy.mov", .texture_count = 1, .formats = &.{fmt_ycocg_dxt5} },
    .{ .path = "tests/fixtures/hapy_chunked.mov", .texture_count = 1, .formats = &.{fmt_ycocg_dxt5} },
    .{ .path = "tests/fixtures/hap7.mov", .texture_count = 1, .formats = &.{fmt_bc7} },
    .{ .path = "tests/fixtures/hapm.mov", .texture_count = 2, .formats = &.{ fmt_ycocg_dxt5, fmt_rgtc1 } },
};

/// Open a fixture through the C ABI, skipping the test if the file isn't
/// present (fixtures are committed, so this only trips in a stripped
/// checkout). Named apart from test_support.openFixture, which opens a
/// fixture one layer down, as a reader plus demuxer.
fn openFixtureHandle(path: [:0]const u8) !*Handle {
    var handle: ?*Handle = null;
    const err = abi.hap_open(path.ptr, &handle);
    if (err == ok(.file_not_found)) return error.SkipZigTest;
    try testing.expectEqual(ok(.ok), err);
    return handle.?;
}

/// True if `bytes` isn't all zeroes -- a decoded texture that came back empty
/// would otherwise pass a size-only assertion.
fn hasContent(bytes: []const u8) bool {
    for (bytes) |b| {
        if (b != 0) return true;
    }
    return false;
}

test "hap_open exposes track metadata and texture layout for every variant" {
    for (fixtures) |fixture| {
        const handle = try openFixtureHandle(fixture.path);
        defer abi.hap_close(handle);

        try testing.expectEqual(@as(i32, 640), abi.hap_get_width(handle));
        try testing.expectEqual(@as(i32, 360), abi.hap_get_height(handle));
        try testing.expect(abi.hap_get_frame_count(handle) > 0);
        try testing.expectApproxEqAbs(@as(f32, 30.0), abi.hap_get_frame_rate(handle), 0.01);

        try testing.expectEqual(fixture.texture_count, abi.hap_get_texture_count(handle));

        const blocks: i32 = (640 / 4) * (360 / 4);
        for (fixture.formats, 0..) |expected_format, i| {
            const tex: i32 = @intCast(i);
            try testing.expectEqual(expected_format, abi.hap_get_texture_format(handle, tex));

            const block_bytes: i32 = switch (expected_format) {
                fmt_dxt1, fmt_rgtc1 => 8,
                else => 16,
            };
            try testing.expectEqual(blocks * block_bytes, abi.hap_get_texture_buffer_size(handle, tex));
        }

        // One past the last texture is out of range, not a silent zero-size
        // texture.
        try testing.expectEqual(@as(i32, 0), abi.hap_get_texture_format(handle, fixture.texture_count));
        try testing.expectEqual(@as(i32, 0), abi.hap_get_texture_buffer_size(handle, fixture.texture_count));
    }
}

test "hap_decode_texture decodes every frame of every variant fixture" {
    for (fixtures) |fixture| {
        const handle = try openFixtureHandle(fixture.path);
        defer abi.hap_close(handle);

        const frame_count = abi.hap_get_frame_count(handle);
        const texture_count = abi.hap_get_texture_count(handle);

        var tex: i32 = 0;
        while (tex < texture_count) : (tex += 1) {
            const size = abi.hap_get_texture_buffer_size(handle, tex);
            try testing.expect(size > 0);

            const buf = try testing.allocator.alloc(u8, @intCast(size));
            defer testing.allocator.free(buf);

            var frame: i32 = 0;
            while (frame < frame_count) : (frame += 1) {
                @memset(buf, 0);
                try testing.expectEqual(
                    ok(.ok),
                    abi.hap_decode_texture(handle, frame, tex, buf.ptr, size),
                );
                try testing.expect(hasContent(buf));
            }
        }
    }
}

test "hap_decode_texture returns Hap Q Alpha's two textures as distinct content" {
    const handle = try openFixtureHandle("tests/fixtures/hapm.mov");
    defer abi.hap_close(handle);

    try testing.expectEqual(@as(i32, 2), abi.hap_get_texture_count(handle));

    const size0 = abi.hap_get_texture_buffer_size(handle, 0);
    const size1 = abi.hap_get_texture_buffer_size(handle, 1);
    const color = try testing.allocator.alloc(u8, @intCast(size0));
    defer testing.allocator.free(color);
    const alpha = try testing.allocator.alloc(u8, @intCast(size1));
    defer testing.allocator.free(alpha);

    // Decode texture 0 then texture 1 of the same frame: the second call
    // must hit the handle's cached sample and still decode its *own*
    // texture (the reference plugin's bug was decoding texture 0 twice).
    try testing.expectEqual(ok(.ok), abi.hap_decode_texture(handle, 0, 0, color.ptr, size0));
    try testing.expectEqual(ok(.ok), abi.hap_decode_texture(handle, 0, 1, alpha.ptr, size1));

    try testing.expect(hasContent(color));
    try testing.expect(hasContent(alpha));
    // Different sizes already prove they aren't the same buffer; compare the
    // common prefix too, so a same-size variant of the bug can't slip by.
    const prefix = @min(color.len, alpha.len);
    try testing.expect(!std.mem.eql(u8, color[0..prefix], alpha[0..prefix]));

    // And both match the committed golden textures byte for byte.
    try test_support.expectMatchesGolden("tests/fixtures/hapm_golden_tex0.bin", color);
    try test_support.expectMatchesGolden("tests/fixtures/hapm_golden_tex1.bin", alpha);
}

test "hap_decode_texture matches the committed Hap golden texture" {
    const handle = try openFixtureHandle("tests/fixtures/hap1.mov");
    defer abi.hap_close(handle);

    const size = abi.hap_get_texture_buffer_size(handle, 0);
    const buf = try testing.allocator.alloc(u8, @intCast(size));
    defer testing.allocator.free(buf);

    try testing.expectEqual(ok(.ok), abi.hap_decode_texture(handle, 0, 0, buf.ptr, size));

    try test_support.expectMatchesGolden("tests/fixtures/hap1_golden.bin", buf);
}

test "hap_open reports a missing file as HAP_ERROR_FILE_NOT_FOUND" {
    var handle: ?*Handle = null;
    try testing.expectEqual(
        ok(.file_not_found),
        abi.hap_open("tests/fixtures/does_not_exist.mov", &handle),
    );
    try testing.expectEqual(@as(?*Handle, null), handle);
}

test "hap_open rejects a corrupt file without producing a handle" {
    // A fuzzer-found crash input: a real file whose bytes are not a usable
    // MOV container.
    var handle: ?*Handle = null;
    const err = abi.hap_open("tests/fixtures/fuzz_regressions/crash_36735b5f.bin", &handle);
    try testing.expect(err != ok(.ok));
    try testing.expectEqual(@as(?*Handle, null), handle);

    // Whatever the specific rejection, it must be one of the container-level
    // failures, not a file/argument error.
    const container_errors = [_]i32{
        ok(.not_a_mov),
        ok(.no_hap_track),
        ok(.unsupported_variant),
        ok(.corrupt_track),
        ok(.invalid_frame),
    };
    try testing.expect(std.mem.indexOfScalar(i32, &container_errors, err) != null);
}

test "hap_open validates its arguments" {
    var handle: ?*Handle = null;
    try testing.expectEqual(ok(.invalid_argument), abi.hap_open(null, &handle));
    try testing.expectEqual(ok(.invalid_argument), abi.hap_open("", &handle));
    try testing.expectEqual(ok(.invalid_argument), abi.hap_open("tests/fixtures/hap1.mov", null));
}

test "the getters tolerate a null handle and hap_close accepts one" {
    try testing.expectEqual(@as(i32, 0), abi.hap_get_width(null));
    try testing.expectEqual(@as(i32, 0), abi.hap_get_height(null));
    try testing.expectEqual(@as(i32, 0), abi.hap_get_frame_count(null));
    try testing.expectEqual(@as(f32, 0), abi.hap_get_frame_rate(null));
    try testing.expectEqual(@as(i32, 0), abi.hap_get_texture_count(null));
    try testing.expectEqual(@as(i32, 0), abi.hap_get_texture_format(null, 0));
    try testing.expectEqual(@as(i32, 0), abi.hap_get_texture_buffer_size(null, 0));
    abi.hap_close(null);
}

test "hap_decode_texture rejects bad frames, textures and buffers" {
    const handle = try openFixtureHandle("tests/fixtures/hap1.mov");
    defer abi.hap_close(handle);

    const size = abi.hap_get_texture_buffer_size(handle, 0);
    const buf = try testing.allocator.alloc(u8, @intCast(size));
    defer testing.allocator.free(buf);

    const frame_count = abi.hap_get_frame_count(handle);

    try testing.expectEqual(
        ok(.frame_out_of_range),
        abi.hap_decode_texture(handle, frame_count, 0, buf.ptr, size),
    );
    try testing.expectEqual(
        ok(.frame_out_of_range),
        abi.hap_decode_texture(handle, -1, 0, buf.ptr, size),
    );
    try testing.expectEqual(
        ok(.invalid_argument),
        abi.hap_decode_texture(handle, 0, 1, buf.ptr, size),
    );
    try testing.expectEqual(
        ok(.invalid_argument),
        abi.hap_decode_texture(handle, 0, 0, null, size),
    );
    try testing.expectEqual(
        ok(.invalid_argument),
        abi.hap_decode_texture(null, 0, 0, buf.ptr, size),
    );
    try testing.expectEqual(
        ok(.buffer_too_small),
        abi.hap_decode_texture(handle, 0, 0, buf.ptr, size - 1),
    );

    // A buffer larger than the texture is fine.
    const big = try testing.allocator.alloc(u8, @as(usize, @intCast(size)) + 64);
    defer testing.allocator.free(big);
    try testing.expectEqual(
        ok(.ok),
        abi.hap_decode_texture(handle, 0, 0, big.ptr, @intCast(big.len)),
    );
}

/// Counts bytes handed out by the plugin's internal allocator, so a test can
/// prove a decode never allocates a staging buffer for the texture.
const CountingAllocator = struct {
    backing: std.mem.Allocator,
    allocated: usize = 0,

    const vtable: std.mem.Allocator.VTable = .{
        .alloc = alloc,
        .resize = resize,
        .remap = remap,
        .free = free,
    };

    fn allocator(self: *CountingAllocator) std.mem.Allocator {
        return .{ .ptr = self, .vtable = &vtable };
    }

    fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ra: usize) ?[*]u8 {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        const p = self.backing.rawAlloc(len, alignment, ra);
        if (p != null) self.allocated += len;
        return p;
    }

    fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ra: usize) bool {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        return self.backing.rawResize(memory, alignment, new_len, ra);
    }

    fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ra: usize) ?[*]u8 {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        const p = self.backing.rawRemap(memory, alignment, new_len, ra);
        if (p != null and new_len > memory.len) self.allocated += new_len - memory.len;
        return p;
    }

    fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ra: usize) void {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        self.backing.rawFree(memory, alignment, ra);
    }
};

test "decoding allocates nothing for the texture payload itself" {
    var counter: CountingAllocator = .{ .backing = testing.allocator };
    const previous = abi.allocator;
    abi.allocator = counter.allocator();
    defer abi.allocator = previous;

    for ([_][:0]const u8{
        "tests/fixtures/hapy.mov", // single Snappy block
        "tests/fixtures/hapy_chunked.mov", // Complex: parallel chunk decode
        "tests/fixtures/hapm.mov", // two textures out of one sample
    }) |path| {
        const handle = try openFixtureHandle(path);
        defer abi.hap_close(handle);

        var tex: i32 = 0;
        while (tex < abi.hap_get_texture_count(handle)) : (tex += 1) {
            const size: usize = @intCast(abi.hap_get_texture_buffer_size(handle, tex));
            const buf = try testing.allocator.alloc(u8, size);
            defer testing.allocator.free(buf);

            counter.allocated = 0;
            try testing.expectEqual(
                ok(.ok),
                abi.hap_decode_texture(handle, 0, tex, buf.ptr, @intCast(size)),
            );

            // Only the chunk-plan array (a few hundred bytes at most) may be
            // allocated; a staging buffer for the decoded texture would show
            // up here as a `size`-sized allocation.
            try testing.expect(counter.allocated < size / 16);
        }
    }
}

test "hap_decode_texture writes into the caller's buffer, not past it" {
    // Decode into the middle of a larger allocation with guard bytes on
    // either side: the decoder is handed only the inner slice, so any write
    // outside it (or a stray free of caller memory) shows up here.
    const guard_len = 4096;
    for ([_][:0]const u8{
        "tests/fixtures/hap1.mov", // single Snappy block
        "tests/fixtures/hap1_chunked.mov", // Complex/chunked, parallel chunks
    }) |path| {
        const handle = try openFixtureHandle(path);
        defer abi.hap_close(handle);

        const size: usize = @intCast(abi.hap_get_texture_buffer_size(handle, 0));
        const backing = try testing.allocator.alloc(u8, size + 2 * guard_len);
        defer testing.allocator.free(backing);
        @memset(backing, 0xA5);

        const target = backing[guard_len..][0..size];
        try testing.expectEqual(
            ok(.ok),
            abi.hap_decode_texture(handle, 0, 0, target.ptr, @intCast(size)),
        );

        try testing.expect(hasContent(target));
        for (backing[0..guard_len]) |b| try testing.expectEqual(@as(u8, 0xA5), b);
        for (backing[guard_len + size ..]) |b| try testing.expectEqual(@as(u8, 0xA5), b);
    }
}

test "a too-small buffer is refused for chunked frames without leaking or overrunning" {
    // The undersized case is the one where the decoder's output can no
    // longer live in the caller's buffer: it must fall back to memory of its
    // own and release it (the testing allocator fails the test otherwise)
    // rather than growing the caller's. The buffer's *contents* are
    // explicitly unspecified after an error, so only its bounds are checked
    // here -- via guard bytes on either side.
    const handle = try openFixtureHandle("tests/fixtures/hapy_chunked.mov");
    defer abi.hap_close(handle);

    const guard_len = 1024;
    const half: usize = @as(usize, @intCast(abi.hap_get_texture_buffer_size(handle, 0))) / 2;
    const backing = try testing.allocator.alloc(u8, half + 2 * guard_len);
    defer testing.allocator.free(backing);
    @memset(backing, 0x5A);

    const small = backing[guard_len..][0..half];
    try testing.expectEqual(
        ok(.buffer_too_small),
        abi.hap_decode_texture(handle, 0, 0, small.ptr, @intCast(small.len)),
    );

    for (backing[0..guard_len]) |b| try testing.expectEqual(@as(u8, 0x5A), b);
    for (backing[guard_len + half ..]) |b| try testing.expectEqual(@as(u8, 0x5A), b);
}

test "hap_set_thread_count retunes chunk decode without changing its output" {
    try testing.expectEqual(ok(.invalid_argument), abi.hap_set_thread_count(0));
    try testing.expectEqual(ok(.invalid_argument), abi.hap_set_thread_count(-4));

    const handle = try openFixtureHandle("tests/fixtures/hap1_chunked.mov");
    defer abi.hap_close(handle);

    const size = abi.hap_get_texture_buffer_size(handle, 0);
    const single = try testing.allocator.alloc(u8, @intCast(size));
    defer testing.allocator.free(single);
    const parallel = try testing.allocator.alloc(u8, @intCast(size));
    defer testing.allocator.free(parallel);

    // Serial (calling thread only) and heavily parallel decodes of the same
    // chunked frame must agree byte for byte.
    try testing.expectEqual(ok(.ok), abi.hap_set_thread_count(1));
    try testing.expectEqual(ok(.ok), abi.hap_decode_texture(handle, 3, 0, single.ptr, size));

    try testing.expectEqual(ok(.ok), abi.hap_set_thread_count(64));
    try testing.expectEqual(ok(.ok), abi.hap_decode_texture(handle, 3, 0, parallel.ptr, size));

    try testing.expect(hasContent(single));
    try testing.expectEqualSlices(u8, single, parallel);
}
