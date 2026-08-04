//! bluecadet_hap.zig
//!
//! The C ABI exported by the `bluecadet_hap` native plugin, wrapping the
//! engine-agnostic demux/decode core in `src/core/`. `src/bluecadet_hap.h`
//! is the hand-maintained mirror of everything declared here and is what the
//! C# bindings are written against -- keep the two in sync.
//!
//! Contract, in one place:
//!
//!  * Every fallible entry point returns an `int32_t` error code from the
//!    `HapError` enum; `HAP_OK` (0) is success. Getters return 0 for a null
//!    or unopened handle rather than an error code.
//!  * A handle owns everything reachable from it (the mmap of the file, the
//!    cached sample table, the decode scratch buffer). `hap_close` releases
//!    all of it; passing null is a no-op.
//!  * Per-handle calls must be serialized by the caller -- the intended use
//!    is one decode thread per open file, which is what the C# layer does.
//!    Different handles may be used concurrently from different threads with
//!    no coordination.
//!  * `hap_set_thread_count` is process-global (it retunes the shared
//!    chunk-decode pool) and is safe to call from any thread at any time.
//!
//! Texture model: a frame carries one texture for Hap/Hap Alpha/Hap Q/Hap R
//! and two for Hap Q Alpha (color + alpha). `hap_get_texture_count`,
//! `hap_get_texture_format`, `hap_get_texture_buffer_size` and
//! `hap_decode_texture` are all indexed by texture, so the caller uploads
//! each texture to its own GPU resource. Both textures of one frame decode
//! from the same demuxed sample, which the handle caches between the two
//! calls.

const std = @import("std");
const builtin = @import("builtin");

const core = @import("core/core.zig");

const Demuxer = core.demuxer.Demuxer;
const MmapReader = core.mmap_reader.MmapReader;
const HapTextureFormat = core.hap_frame.HapTextureFormat;

/// Everything a handle owns comes from here. libc's allocator is thread-safe
/// (handles are independent and may be driven from different threads) and
/// needs no process-wide state of its own; test builds swap in the testing
/// allocator so the suite fails on a leaked handle.
const allocator: std.mem.Allocator = if (builtin.is_test)
    std.testing.allocator
else
    std.heap.c_allocator;

/// Hap Q Alpha carries a color plus an alpha texture; nothing carries more.
const max_textures = core.hap_frame.max_textures;

// -----------------------------------------------------------------------
// Error codes (mirror of HapError in bluecadet_hap.h).
// -----------------------------------------------------------------------

pub const HapError = enum(i32) {
    ok = 0,
    /// A null pointer, an out-of-range index, or a nonsensical count.
    invalid_argument = 1,
    /// The path could not be opened (missing file, no permission).
    file_not_found = 2,
    /// The file exists but could not be stat'd or memory-mapped.
    file_read = 3,
    /// Not a parseable MP4/MOV container.
    not_a_mov = 4,
    /// A container, but with no Hap video track in it.
    no_hap_track = 5,
    /// A Hap track whose variant this plugin cannot decode (HapA, Hap HDR).
    unsupported_variant = 6,
    /// The Hap track's sample table is empty or inconsistent with the file.
    corrupt_track = 7,
    /// `frame_index` is outside `[0, hap_get_frame_count)`.
    frame_out_of_range = 8,
    /// The frame's bytes are not a valid/supported Hap frame.
    invalid_frame = 9,
    /// The supplied buffer is smaller than the decoded texture.
    buffer_too_small = 10,
    /// An allocation failed.
    out_of_memory = 11,
};

/// Texture layout codes returned by `hap_get_texture_format` (mirror of
/// HapTextureFormatCode in bluecadet_hap.h -- the values are ABI).
pub const HapTextureFormatCode = enum(i32) {
    /// BC1 -- Hap.
    dxt1 = 1,
    /// BC3 -- Hap Alpha.
    dxt5 = 2,
    /// BC7 -- Hap R.
    bc7 = 3,
    /// BC3 carrying scaled YCoCg -- Hap Q, Hap Q Alpha texture 0.
    ycocg_dxt5 = 4,
    /// BC4 -- Hap Q Alpha texture 1 (alpha).
    rgtc1 = 5,
};

fn code(err: HapError) i32 {
    return @intFromEnum(err);
}

fn formatCode(format: HapTextureFormat) i32 {
    const format_code: HapTextureFormatCode = switch (format) {
        .rgb_dxt1 => .dxt1,
        .rgba_dxt5 => .dxt5,
        .rgba_bptc_unorm => .bc7,
        .ycocg_dxt5 => .ycocg_dxt5,
        .a_rgtc1 => .rgtc1,
    };
    return @intFromEnum(format_code);
}

// -----------------------------------------------------------------------
// Decoded texture sizing. The one place block math lives.
// -----------------------------------------------------------------------

/// Compressed bytes per 4x4 block for a texture format.
fn blockBytes(format: HapTextureFormat) u32 {
    return switch (format) {
        .rgb_dxt1, .a_rgtc1 => 8,
        .rgba_dxt5, .ycocg_dxt5, .rgba_bptc_unorm => 16,
    };
}

/// Decoded byte size of one `width` x `height` texture in `format`: whole
/// 4x4 blocks, rounding up on each axis for dimensions that aren't
/// multiples of 4.
fn frameBytes(format: HapTextureFormat, width: u32, height: u32) u32 {
    return ((width + 3) / 4) * ((height + 3) / 4) * blockBytes(format);
}

// -----------------------------------------------------------------------
// Handle.
// -----------------------------------------------------------------------

/// Opaque to C (`HapHandle`); public here only so the ABI test suite can
/// name the pointer type the exports take.
pub const Handle = struct {
    /// The allocator everything below was allocated from, captured at open
    /// time so `destroy` releases it all through the same one even if the
    /// process-wide `allocator` has since been swapped.
    alloc: std.mem.Allocator,

    reader: MmapReader,
    demux: Demuxer,

    texture_count: u32,
    formats: [max_textures]HapTextureFormat,
    buffer_sizes: [max_textures]u32,

    /// Decode destination, reused across frames so a steady-state decode
    /// loop does no allocation.
    scratch: std.ArrayListUnmanaged(u8) = .empty,

    /// Last demuxed sample, kept so decoding texture 1 of a Hap Q Alpha
    /// frame right after texture 0 doesn't walk the sample table again.
    /// Negative when nothing is cached.
    cached_index: i64 = -1,
    cached_sample: []const u8 = &.{},

    fn sample(self: *Handle, frame_index: u32) ?[]const u8 {
        if (self.cached_index == @as(i64, frame_index)) return self.cached_sample;
        const data = self.demux.sampleData(&self.reader, frame_index) orelse return null;
        self.cached_index = frame_index;
        self.cached_sample = data;
        return data;
    }

    fn destroy(self: *Handle) void {
        const alloc = self.alloc; // copied out: `self` is gone by the last line
        self.scratch.deinit(alloc);
        self.demux.deinit(alloc);
        self.reader.deinit();
        alloc.destroy(self);
    }
};

/// Everything `openHandle` can fail with. Keeping it a Zig error union
/// (rather than returning codes directly) is what lets the open path clean
/// up with `errdefer`.
const OpenFailure = MmapReader.InitError || core.demuxer.OpenError ||
    error{ InvalidFrame, MissingFirstSample };

/// Every error any exported entry point can see. `errorCode` is the single
/// error-to-HapError mapping, so an error that both open and decode can
/// raise cannot be reported as two different codes.
const CallFailure = OpenFailure || core.hap_decode.DecodeError;

fn errorCode(err: CallFailure) HapError {
    return switch (err) {
        error.OpenFailed => .file_not_found,
        error.StatFailed, error.MmapFailed => .file_read,
        error.MalformedMp4, error.NoMoovBox => .not_a_mov,
        error.NoHapTrack => .no_hap_track,
        error.UnsupportedHapVariant => .unsupported_variant,
        error.ZeroSamples,
        error.TooManySamples,
        error.SamplesExceedFileSize,
        error.MissingFirstSample,
        => .corrupt_track,
        error.InvalidFrame => .invalid_frame,
        error.OutOfMemory => .out_of_memory,
    };
}

/// Read the frame-0 texture layout (count, per-texture format and decoded
/// size) that the metadata getters answer from. Formats are per-frame data
/// in the Hap bitstream, so they are read from the file rather than assumed
/// from the track's FourCC.
fn readTextureLayout(handle: *Handle) error{ InvalidFrame, MissingFirstSample }!void {
    const first = handle.sample(0) orelse return error.MissingFirstSample;

    const count = try core.hap_decode.frameTextureCount(first);
    if (count == 0 or count > max_textures) return error.InvalidFrame;

    const width = handle.demux.track.width;
    const height = handle.demux.track.height;

    var i: u32 = 0;
    while (i < count) : (i += 1) {
        const format = try core.hap_decode.frameTextureFormat(first, i);
        handle.formats[i] = format;
        handle.buffer_sizes[i] = frameBytes(format, width, height);
    }

    handle.texture_count = count;
}

// -----------------------------------------------------------------------
// Lifecycle.
// -----------------------------------------------------------------------

fn openHandle(path: []const u8) OpenFailure!*Handle {
    const handle = try allocator.create(Handle);
    errdefer allocator.destroy(handle);

    // `reader` and `handle.reader` are two copies of the same mapping;
    // unmapping either one releases it, so only one cleanup path runs.
    var reader = try MmapReader.init(path);
    errdefer reader.deinit();

    handle.* = .{
        .alloc = allocator,
        .reader = reader,
        .demux = .{},
        .texture_count = 0,
        .formats = .{ .rgb_dxt1, .rgb_dxt1 },
        .buffer_sizes = .{ 0, 0 },
    };
    errdefer handle.demux.deinit(allocator);

    try handle.demux.open(allocator, &handle.reader);
    try readTextureLayout(handle);

    return handle;
}

/// Open a Hap MOV file. On success writes the new handle to `out_handle`
/// and returns HAP_OK; on failure writes null and returns the reason.
pub export fn hap_open(path: ?[*:0]const u8, out_handle: ?*?*Handle) i32 {
    const out = out_handle orelse return code(.invalid_argument);
    out.* = null;

    const path_z = path orelse return code(.invalid_argument);
    const path_slice = std.mem.span(path_z);
    if (path_slice.len == 0) return code(.invalid_argument);

    out.* = openHandle(path_slice) catch |err| return code(errorCode(err));
    return code(.ok);
}

/// Release a handle and everything it owns. Null is a no-op.
pub export fn hap_close(handle: ?*Handle) void {
    const h = handle orelse return;
    h.destroy();
}

// -----------------------------------------------------------------------
// Metadata.
// -----------------------------------------------------------------------

pub export fn hap_get_width(handle: ?*Handle) i32 {
    const h = handle orelse return 0;
    return @intCast(h.demux.track.width);
}

pub export fn hap_get_height(handle: ?*Handle) i32 {
    const h = handle orelse return 0;
    return @intCast(h.demux.track.height);
}

pub export fn hap_get_frame_count(handle: ?*Handle) i32 {
    const h = handle orelse return 0;
    return @intCast(h.demux.track.frame_count);
}

pub export fn hap_get_frame_rate(handle: ?*Handle) f32 {
    const h = handle orelse return 0;
    return @floatCast(h.demux.track.frame_rate);
}

/// Number of textures each frame carries: 1, or 2 for Hap Q Alpha.
pub export fn hap_get_texture_count(handle: ?*Handle) i32 {
    const h = handle orelse return 0;
    return @intCast(h.texture_count);
}

/// HapTextureFormatCode of texture `tex_index`, or 0 if the index is out of
/// range.
pub export fn hap_get_texture_format(handle: ?*Handle, tex_index: i32) i32 {
    const h = handle orelse return 0;
    if (tex_index < 0 or @as(u32, @intCast(tex_index)) >= h.texture_count) return 0;
    return formatCode(h.formats[@intCast(tex_index)]);
}

/// Decoded byte size of texture `tex_index` -- the buffer size
/// `hap_decode_texture` needs -- or 0 if the index is out of range.
pub export fn hap_get_texture_buffer_size(handle: ?*Handle, tex_index: i32) i32 {
    const h = handle orelse return 0;
    if (tex_index < 0 or @as(u32, @intCast(tex_index)) >= h.texture_count) return 0;
    return @intCast(h.buffer_sizes[@intCast(tex_index)]);
}

// -----------------------------------------------------------------------
// Decode.
// -----------------------------------------------------------------------

/// Decode texture `tex_index` of frame `frame_index` into `buf`.
pub export fn hap_decode_texture(
    handle: ?*Handle,
    frame_index: i32,
    tex_index: i32,
    buf: ?[*]u8,
    buf_size: i32,
) i32 {
    const h = handle orelse return code(.invalid_argument);
    const dst = buf orelse return code(.invalid_argument);
    if (buf_size <= 0) return code(.invalid_argument);
    if (tex_index < 0 or @as(u32, @intCast(tex_index)) >= h.texture_count) return code(.invalid_argument);
    if (frame_index < 0 or @as(u32, @intCast(frame_index)) >= h.demux.track.frame_count) return code(.frame_out_of_range);

    const data = h.sample(@intCast(frame_index)) orelse return code(.frame_out_of_range);

    _ = core.hap_decode.decodeTexture(allocator, data, @intCast(tex_index), &h.scratch) catch |err|
        return code(errorCode(err));

    const decoded = h.scratch.items;
    if (decoded.len > @as(usize, @intCast(buf_size))) return code(.buffer_too_small);

    @memcpy(dst[0..decoded.len], decoded);
    return code(.ok);
}

// -----------------------------------------------------------------------
// Threading.
// -----------------------------------------------------------------------

/// Set how many threads decode a chunked frame's chunks in parallel,
/// process-wide. `thread_count` counts the calling decode thread, which
/// always decodes a share itself, so 1 means "no helper threads". Values
/// above the shared pool's size are clamped to it. Takes effect on the next
/// chunked frame decoded.
pub export fn hap_set_thread_count(thread_count: i32) i32 {
    if (thread_count < 1) return code(.invalid_argument);
    const helpers: u32 = @intCast(thread_count - 1);
    core.thread_pool.instance().setActiveWorkerCount(helpers);
    return code(.ok);
}

test {
    _ = core;
    _ = @import("bluecadet_hap_test.zig");
}
