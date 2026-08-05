//! hap_decode.zig
//!
//! Clean-room Hap frame parser and decoder, implemented from the Hap
//! bitstream specification (HapVideoDRAFT.md). Parses a single compressed
//! Hap frame -- its section headers, texture format, and (for the Complex
//! compressor) its chunk decode-instructions -- and decompresses one
//! texture at a time into a caller-provided buffer.
//!
//! Three second-stage compressors are handled: None (raw copy), Snappy
//! (single block), and Complex (per-chunk None/Snappy, decompressed in
//! parallel via the shared InnerThreadPool). Supported texture formats are
//! the five with a `hap_frame.HapTextureFormat` tag; the two Hap HDR (BC6H)
//! format nibbles are rejected as invalid, matching the demuxer's rejection
//! of HapHDR -- decoding HDR is a stated limitation.
//!
//! Deliberate behavior changes from the reference C decoder:
//!  * A Complex frame whose decode instructions yield a chunk count of zero
//!    is rejected as an invalid frame. The reference code silently
//!    "succeeds" producing zero output bytes; we treat that as malformed.
//!  * Every chunk's compressed span is bounds-checked against the frame
//!    data before it is read. The reference code trusts the size/offset
//!    tables and can read out of bounds on a malformed frame.

const std = @import("std");

const hap_frame = @import("hap_frame.zig");
const thread_pool = @import("thread_pool.zig");

const HapTextureFormat = hap_frame.HapTextureFormat;

pub const Error = error{InvalidFrame};
pub const DecodeError = Error || std.mem.Allocator.Error;

// -----------------------------------------------------------------------
// Snappy C API (hand-declared, no @cImport per project convention).
// -----------------------------------------------------------------------

/// snappy_status values from thirdparty/snappy/snappy-c.h.
const snappy_ok: c_int = 0;

extern fn snappy_uncompress(
    compressed: [*]const u8,
    compressed_length: usize,
    uncompressed: [*]u8,
    uncompressed_length: *usize,
) c_int;

extern fn snappy_uncompressed_length(
    compressed: [*]const u8,
    compressed_length: usize,
    result: *usize,
) c_int;

/// Uncompressed byte length declared by a Snappy block's header, without
/// decompressing it. A block whose header doesn't parse is a bad frame.
fn snappyLength(src: []const u8) Error!usize {
    var len: usize = 0;
    if (snappy_uncompressed_length(src.ptr, src.len, &len) != snappy_ok) return error.InvalidFrame;
    return len;
}

/// Decompress the Snappy block `src` into `dst`, which must be exactly the
/// block's uncompressed length (i.e. sized from `snappyLength` on the same
/// bytes). Both are checked: snappy_uncompress refuses a `dst` that is too
/// small, and the length it reports back is required to fill `dst` exactly,
/// so a short write can never leave part of `dst` unwritten.
fn snappyDecompressExact(src: []const u8, dst: []u8) Error!void {
    var out_len: usize = dst.len;
    if (snappy_uncompress(src.ptr, src.len, dst.ptr, &out_len) != snappy_ok) return error.InvalidFrame;
    if (out_len != dst.len) return error.InvalidFrame;
}

// -----------------------------------------------------------------------
// Wire-format constants (from the Hap spec).
// -----------------------------------------------------------------------

/// Top nibble of a texture section's type byte: the second-stage compressor.
const compressor_none: u8 = 0xA;
const compressor_snappy: u8 = 0xB;
const compressor_complex: u8 = 0xC;

/// Section type bytes.
const section_multi_image: u8 = 0x0D;
const section_decode_instructions: u8 = 0x01;
const section_chunk_compressor_table: u8 = 0x02;
const section_chunk_size_table: u8 = 0x03;
const section_chunk_offset_table: u8 = 0x04;

// -----------------------------------------------------------------------
// Little-endian scalar reads.
// -----------------------------------------------------------------------

/// Read a 24-bit little-endian unsigned int. Caller guarantees >= 3 bytes.
fn readU24(b: []const u8) u32 {
    return @as(u32, b[0]) | (@as(u32, b[1]) << 8) | (@as(u32, b[2]) << 16);
}

/// Read a 32-bit little-endian unsigned int. Caller guarantees >= 4 bytes.
fn readU32(b: []const u8) u32 {
    return std.mem.readInt(u32, b[0..4], .little);
}

// -----------------------------------------------------------------------
// Section header parsing.
// -----------------------------------------------------------------------

/// A parsed Hap section header. `size` is the payload length excluding the
/// header; the payload occupies `buf[header_len .. header_len + size]`.
const SectionHeader = struct {
    header_len: usize,
    size: usize,
    type: u8,
};

/// Parse the section header at the start of `buf`. The 24-bit size lives in
/// bytes 0..2; a size of zero selects the 8-byte extended form whose real
/// size is a 32-bit LE value at bytes 4..7. The type byte is always at
/// offset 3. Enforces that `header_len + size` fits within `buf`.
///
/// Free-standing (and public) so hap_decode_test.zig can pin the header
/// grammar directly, the way demuxer.zig exposes `parseStsd`.
pub fn readSectionHeader(buf: []const u8) Error!SectionHeader {
    if (buf.len < 4) return error.InvalidFrame;

    var size: usize = readU24(buf);
    var header_len: usize = 4;
    if (size == 0) {
        if (buf.len < 8) return error.InvalidFrame;
        size = readU32(buf[4..8]);
        header_len = 8;
    }
    const type_byte = buf[3];

    if (size > buf.len - header_len) return error.InvalidFrame;

    return .{ .header_len = header_len, .size = size, .type = type_byte };
}

/// A located texture section: its payload bytes and its packed type byte
/// (compressor nibble in the high 4 bits, format nibble in the low 4).
const TextureSection = struct {
    payload: []const u8,
    type: u8,
};

/// Iterates the back-to-back sub-sections packed into a Multi-Image (HapM)
/// section's body, in the mold of demuxer.zig's `BoxIter`: the "advance by
/// header_len + size" walk lives here once, so every caller that counts
/// sub-sections and every caller that indexes them agree by construction.
///
/// `next` yields sections until the body is exhausted; a header that
/// doesn't parse (or whose payload overruns the body) is an error, not an
/// end, since it means the frame is malformed rather than finished.
const SubSectionIter = struct {
    body: []const u8,
    pos: usize = 0,

    fn next(it: *SubSectionIter) Error!?TextureSection {
        if (it.pos >= it.body.len) return null;
        const sub = try readSectionHeader(it.body[it.pos..]);
        const start = it.pos + sub.header_len;
        it.pos = start + sub.size;
        return .{ .payload = it.body[start..][0..sub.size], .type = sub.type };
    }
};

/// Iterate the sub-sections of a Multi-Image frame whose top-level header
/// has already been parsed.
fn subSections(frame: []const u8, top: SectionHeader) SubSectionIter {
    return .{ .body = frame[top.header_len..][0..top.size] };
}

/// Locate the texture section at `index`. For a top-level Multi-Image
/// section (HapM) this walks the back-to-back sub-sections; otherwise the
/// single texture is the top-level section and only index 0 is valid.
fn sectionAtIndex(frame: []const u8, index: u32) Error!TextureSection {
    const top = try readSectionHeader(frame);

    if (top.type == section_multi_image) {
        var it = subSections(frame, top);
        var i: u32 = 0;
        while (try it.next()) |sub| : (i += 1) {
            if (i == index) return sub;
        }
        return error.InvalidFrame;
    }

    if (index != 0) return error.InvalidFrame;
    return .{ .payload = frame[top.header_len..][0..top.size], .type = top.type };
}

/// Map a Hap format nibble to a supported texture format. The two Hap HDR
/// (BC6H) nibbles and any unknown nibble are rejected -- HDR decode is a
/// stated limitation.
fn textureFormatFromNibble(nibble: u8) Error!HapTextureFormat {
    return switch (nibble) {
        0xB => .rgb_dxt1,
        0xE => .rgba_dxt5,
        0xF => .ycocg_dxt5,
        0x1 => .a_rgtc1,
        0xC => .rgba_bptc_unorm,
        else => error.InvalidFrame,
    };
}

// -----------------------------------------------------------------------
// Public queries.
// -----------------------------------------------------------------------

/// Number of textures carried by `frame`: 1 for a single-texture frame, or
/// the count of sub-sections in a Multi-Image (HapM) frame. Does not cap the
/// result -- the caller enforces the supported range of
/// 1..=`hap_frame.max_textures`.
pub fn frameTextureCount(frame: []const u8) Error!u32 {
    const top = try readSectionHeader(frame);
    if (top.type != section_multi_image) return 1;

    var it = subSections(frame, top);
    var count: u32 = 0;
    while (try it.next()) |_| count += 1;
    return count;
}

/// Texture format of the texture at `index`.
pub fn frameTextureFormat(frame: []const u8, index: u32) Error!HapTextureFormat {
    const sec = try sectionAtIndex(frame, index);
    return textureFormatFromNibble(sec.type & 0x0F);
}

/// Number of second-stage chunks for the texture at `index`: the parsed
/// chunk count for a Complex texture, or 1 for None/Snappy. Nothing in
/// production decodes per chunk, so this exists purely so hap_decode_test.zig
/// can assert *how* a frame was chunked, not just what it decodes to.
pub fn frameTextureChunkCount(frame: []const u8, index: u32) Error!u32 {
    const sec = try sectionAtIndex(frame, index);
    const compressor = sec.type >> 4;
    return switch (compressor) {
        compressor_complex => (try parseComplexInstructions(sec.payload)).chunk_count,
        compressor_none, compressor_snappy => 1,
        else => error.InvalidFrame,
    };
}

// -----------------------------------------------------------------------
// Complex (chunked) decode instructions.
// -----------------------------------------------------------------------

/// Parsed contents of a Decode Instructions Container. The compressor and
/// size tables are required; the offset table is optional (absent means
/// chunk offsets are the cumulative sums of the size table). `frame_data`
/// is the compressed chunk data following the container.
const ComplexInstructions = struct {
    chunk_count: u32,
    compressors: []const u8,
    sizes: []const u8,
    offsets: ?[]const u8,
    frame_data: []const u8,
};

/// Parse the Decode Instructions Container at the start of a Complex
/// texture section's payload. Sub-sections may appear in any order; unknown
/// ones are skipped. Chunk counts derived from each present table must
/// agree.
fn parseComplexInstructions(payload: []const u8) Error!ComplexInstructions {
    const container = try readSectionHeader(payload);
    if (container.type != section_decode_instructions) return error.InvalidFrame;

    // Frame data begins immediately after the container.
    const frame_data = payload[container.header_len + container.size ..];

    const body = payload[container.header_len..][0..container.size];
    var compressors: ?[]const u8 = null;
    var sizes: ?[]const u8 = null;
    var offsets: ?[]const u8 = null;
    var chunk_count: u32 = 0;

    var offset: usize = 0;
    while (offset < body.len) {
        const sub = try readSectionHeader(body[offset..]);
        const data = body[offset + sub.header_len ..][0..sub.size];

        // `@intCast` to u32 below cannot wrap: `sub.size` is bounded by
        // `readSectionHeader` to fit within the enclosing buffer, which is
        // ultimately a slice of one MP4 sample (see demuxer.zig/hap_frame.zig
        // `SampleEntry.size: u32`, sourced from minimp4's 32-bit stsz/stz2
        // fields) -- so no section can be larger than u32 max to begin with.
        var section_chunk_count: u32 = 0;
        switch (sub.type) {
            section_chunk_compressor_table => {
                compressors = data;
                section_chunk_count = @intCast(sub.size);
            },
            section_chunk_size_table => {
                sizes = data;
                section_chunk_count = @intCast(sub.size / 4);
            },
            section_chunk_offset_table => {
                offsets = data;
                section_chunk_count = @intCast(sub.size / 4);
            },
            else => {}, // skip unknown sub-section
        }

        if (section_chunk_count != 0) {
            if (chunk_count != 0 and section_chunk_count != chunk_count) {
                return error.InvalidFrame;
            }
            chunk_count = section_chunk_count;
        }

        offset += sub.header_len + sub.size;
    }

    if (compressors == null or sizes == null) return error.InvalidFrame;

    return .{
        .chunk_count = chunk_count,
        .compressors = compressors.?,
        .sizes = sizes.?,
        .offsets = offsets,
        .frame_data = frame_data,
    };
}

// -----------------------------------------------------------------------
// Chunked decode worker (driven by the shared InnerThreadPool).
// -----------------------------------------------------------------------

/// One chunk's decode plan. `dst_off`/`dst_len` are the chunk's exact span
/// in the output buffer, both known while planning; `dst` binds that span
/// once the buffer is sized, since resizing may move it.
const ChunkJob = struct {
    compressor: u8,
    src: []const u8,
    dst_off: usize,
    dst_len: usize,
    dst: []u8 = &.{},
    /// Invariant: initialized true while planning (single-threaded, before
    /// the pool fans out) and left alone by a worker that succeeds. A worker
    /// writes this field only on failure -- the rare path -- so steady-state
    /// decoding never has more than one thread writing into a given cache
    /// line of the jobs array (adjacent jobs share lines at this struct's
    /// stride), avoiding cross-thread cache-line ping-pong on every chunk.
    ok: bool = true,
};

/// Grow-only scratch storage for one handle's `ChunkJob` array, reused
/// across every chunked decode on that handle instead of being heap
/// allocated and freed per frame. Safe because a handle is driven serially
/// by one thread and `execute` below is synchronous fork-join: the array is
/// never touched by another decode while a pool batch is in flight. Never
/// shrinks capacity between frames; `resize` only ever grows the backing
/// allocation and cheaply narrows/widens the logical length otherwise.
pub const ChunkScratch = std.ArrayListUnmanaged(ChunkJob);

/// InnerThreadPool work function: decode chunk `index` of the job list `p`
/// points at (a `*[]ChunkJob`). Each invocation touches a distinct job, so
/// no synchronization is needed within a batch -- the pool establishes the
/// happens-before edge around `execute`.
fn decodeChunkWorker(p: ?*anyopaque, index: c_uint) void {
    const jobs: *[]ChunkJob = @ptrCast(@alignCast(p.?));
    const job = &jobs.*[index];
    switch (job.compressor) {
        compressor_snappy => {
            snappyDecompressExact(job.src, job.dst) catch {
                job.ok = false;
                return;
            };
        },
        compressor_none => {
            if (job.src.len == job.dst.len) {
                @memcpy(job.dst, job.src);
            } else {
                job.ok = false;
            }
        },
        else => job.ok = false,
    }
}

/// Decode a Complex (chunked) texture section into `out`, sized exactly to
/// the summed uncompressed chunk lengths. `scratch` is the handle's reused
/// `ChunkJob` buffer; it is resized (grown, never shrunk in capacity) to
/// exactly `n` entries for the duration of this call.
fn decodeComplex(allocator: std.mem.Allocator, payload: []const u8, out: *std.ArrayListUnmanaged(u8), scratch: *ChunkScratch) DecodeError!void {
    const instr = try parseComplexInstructions(payload);
    const n = instr.chunk_count;
    if (n == 0) return error.InvalidFrame;

    try scratch.resize(allocator, n);
    var jobs: []ChunkJob = scratch.items;

    // Plan every chunk: validate its compressor, bound its compressed span
    // against the frame data, and accumulate the exact output size.
    var total: usize = 0;
    var running_compressed: usize = 0;
    for (jobs, 0..) |*job, i| {
        const comp = instr.compressors[i];
        if (comp != compressor_none and comp != compressor_snappy) return error.InvalidFrame;

        const csize: usize = readU32(instr.sizes[i * 4 ..][0..4]);
        const coffset: usize = if (instr.offsets) |o| readU32(o[i * 4 ..][0..4]) else running_compressed;
        running_compressed += csize;

        // Bounds check (an improvement over the reference decoder, which
        // trusts the tables and can read past the frame data).
        if (coffset > instr.frame_data.len or csize > instr.frame_data.len - coffset) {
            return error.InvalidFrame;
        }
        const src = instr.frame_data[coffset..][0..csize];

        const dst_len: usize = switch (comp) {
            compressor_snappy => try snappyLength(src),
            else => csize, // None: uncompressed size == compressed size
        };

        job.* = .{ .compressor = comp, .src = src, .dst_off = total, .dst_len = dst_len };
        total += dst_len;
    }

    try out.resize(allocator, total);

    // Bind destination slices now that the buffer is at its final address.
    for (jobs) |*job| {
        job.dst = out.items[job.dst_off..][0..job.dst_len];
    }

    // count <= 1 decodes inline on the calling thread (no pool involvement).
    // The `@ptrCast` is only because a pointer-to-slice is a double pointer,
    // which Zig won't implicitly erase to `?*anyopaque`.
    thread_pool.instance().execute(decodeChunkWorker, @ptrCast(&jobs), n);

    for (jobs) |job| {
        if (!job.ok) return error.InvalidFrame;
    }
}

// -----------------------------------------------------------------------
// Public single-texture decode.
// -----------------------------------------------------------------------

/// Decode the texture at `index` into `out`, sized exactly to the decoded
/// data, and return its format. `out` is resized (its prior contents are
/// overwritten). On error `out` is left in an unspecified but valid state;
/// callers that need it emptied on failure must clear it themselves (the
/// decoder does, via its errdefer).
pub fn decodeTexture(
    allocator: std.mem.Allocator,
    frame: []const u8,
    index: u32,
    out: *std.ArrayListUnmanaged(u8),
    scratch: *ChunkScratch,
) DecodeError!HapTextureFormat {
    const sec = try sectionAtIndex(frame, index);
    // Validate the texture format before decoding (matches the reference
    // decoder's ordering: a bad format nibble fails before any output).
    const format = try textureFormatFromNibble(sec.type & 0x0F);

    switch (sec.type >> 4) {
        compressor_none => {
            try out.resize(allocator, sec.payload.len);
            @memcpy(out.items, sec.payload);
        },
        compressor_snappy => {
            try out.resize(allocator, try snappyLength(sec.payload));
            try snappyDecompressExact(sec.payload, out.items);
        },
        compressor_complex => try decodeComplex(allocator, sec.payload, out, scratch),
        else => return error.InvalidFrame,
    }

    return format;
}
