//! hap_frame.zig
//!
//! Pure data types shared by the demuxer and decoder: texture format tags,
//! FourCC handling and video-track metadata. No behavior beyond a couple of
//! small helpers.

const std = @import("std");

/// Hap texture format identifiers, matching the HapTextureFormat values
/// from the Hap bitstream spec (GL_EXT_texture_compression_s3tc /
/// GL_ARB_texture_compression_rgtc / GL_ARB_texture_compression_bptc
/// constants). Adding or retiring one means editing the `formatCode` and
/// `blockBytes` switches in bluecadet_hap.zig, which map these onto the
/// codes the C# side turns into Unity texture formats.
pub const HapTextureFormat = enum(u32) {
    rgb_dxt1 = 0x83F0,
    rgba_dxt5 = 0x83F3,
    ycocg_dxt5 = 0x01,
    a_rgtc1 = 0x8DBB,
    rgba_bptc_unorm = 0x8E8C,
};

/// Textures one frame can carry: Hap Q Alpha carries a color plus an alpha
/// texture, and nothing carries more. The single source of truth for that
/// cap -- the decoder's texture queries don't enforce it, so every caller
/// that sizes per-texture storage or validates a texture index bounds
/// itself against this.
pub const max_textures: u32 = 2;

/// FourCC code as a 32-bit integer (big-endian in file, stored host-endian).
pub const FourCC = struct {
    value: u32 = 0,

    pub fn init(value: u32) FourCC {
        return .{ .value = value };
    }

    pub fn initChars(a: u8, b: u8, c: u8, d: u8) FourCC {
        return .{
            .value = (@as(u32, a) << 24) | (@as(u32, b) << 16) |
                (@as(u32, c) << 8) | @as(u32, d),
        };
    }

    pub fn eql(self: FourCC, other: FourCC) bool {
        return self.value == other.value;
    }

    /// Renders the four bytes as ASCII into a stack-allocated fixed array;
    /// no allocation.
    pub fn toString(self: FourCC) [4]u8 {
        return .{
            @truncate(self.value >> 24),
            @truncate(self.value >> 16),
            @truncate(self.value >> 8),
            @truncate(self.value),
        };
    }
};

// Known Hap FourCCs
pub const fcc_hap1 = FourCC.initChars('H', 'a', 'p', '1'); // Hap (BC1/DXT1)
pub const fcc_hap5 = FourCC.initChars('H', 'a', 'p', '5'); // Hap Alpha (BC3/DXT5)
pub const fcc_hapy = FourCC.initChars('H', 'a', 'p', 'Y'); // Hap Q (YCoCg-DXT5)
pub const fcc_hapm = FourCC.initChars('H', 'a', 'p', 'M'); // Hap Q Alpha (dual texture)
pub const fcc_hap7 = FourCC.initChars('H', 'a', 'p', '7'); // Hap R (BC7/BPTC)

// Unsupported Hap FourCCs (used for testing the error path)
pub const fcc_hapa = FourCC.initChars('H', 'a', 'p', 'A'); // HapA (alpha-only, unsupported)
pub const fcc_haphdr = FourCC.initChars('H', 'a', 'p', 'H'); // Hap HDR (BC6, unsupported)

/// Single source of truth for every supported Hap variant.
/// Known-but-unsupported FourCCs intentionally have no enum tag, so they
/// cannot cross the demux boundary as an operational format.
pub const HapVariant = enum {
    hap1, // Hap (BC1/DXT1)
    hap5, // Hap Alpha (BC3/DXT5)
    hapy, // Hap Q (YCoCg-DXT5)
    hapm, // Hap Q Alpha (dual texture)
    hap7, // Hap R (BC7/BPTC)
};

/// Classify a FourCC as a supported Hap variant. Known-but-unsupported and
/// unrelated FourCCs both return null; use isUnsupportedHapFourcc when the
/// demuxer needs to distinguish them for an error message.
pub fn classify(fourcc: FourCC) ?HapVariant {
    return switch (fourcc.value) {
        fcc_hap1.value => .hap1,
        fcc_hap5.value => .hap5,
        fcc_hapy.value => .hapy,
        fcc_hapm.value => .hapm,
        fcc_hap7.value => .hap7,
        else => null,
    };
}

/// Check whether a FourCC identifies a Hap codec this extension recognizes
/// but cannot decode or present.
pub fn isUnsupportedHapFourcc(fourcc: FourCC) bool {
    return switch (fourcc.value) {
        fcc_hapa.value, fcc_haphdr.value => true,
        else => false,
    };
}

/// Parsed track dimensions and FourCC from stsd.
pub const VideoFormat = struct {
    fourcc: FourCC = .{},
    width: u32 = 0,
    height: u32 = 0,
};

/// Video track metadata extracted from the MOV container.
pub const VideoTrackInfo = struct {
    fourcc: FourCC = .{}, // The stsd sample entry FourCC
    width: u32 = 0, // Frame width in pixels
    height: u32 = 0, // Frame height in pixels
    frame_count: u32 = 0, // Number of frames/samples
    frame_rate: f64 = 0.0, // Computed from timescale/duration
    timescale: u32 = 0, // Media timescale (tick rate)
};

/// A cached sample entry: offset into the file and byte size.
pub const SampleEntry = struct {
    offset: u64 = 0,
    size: u32 = 0,
};

test "FourCC.initChars packs bytes big-endian" {
    const fourcc = FourCC.initChars('H', 'a', 'p', '1');
    try std.testing.expectEqual(@as(u32, 0x48617031), fourcc.value);
}

test "FourCC.eql compares by value" {
    const a = FourCC.initChars('H', 'a', 'p', '1');
    const b = FourCC.init(0x48617031);
    const c = FourCC.initChars('H', 'a', 'p', '5');
    try std.testing.expect(a.eql(b));
    try std.testing.expect(!a.eql(c));
}

test "FourCC.toString renders ASCII bytes" {
    const fourcc = FourCC.initChars('H', 'a', 'p', 'Y');
    try std.testing.expectEqualSlices(u8, "HapY", &fourcc.toString());
}

test "classify maps only supported FourCCs to operational variants" {
    try std.testing.expectEqual(HapVariant.hap1, classify(fcc_hap1).?);
    try std.testing.expectEqual(HapVariant.hap5, classify(fcc_hap5).?);
    try std.testing.expectEqual(HapVariant.hapy, classify(fcc_hapy).?);
    try std.testing.expectEqual(HapVariant.hapm, classify(fcc_hapm).?);
    try std.testing.expectEqual(HapVariant.hap7, classify(fcc_hap7).?);
    try std.testing.expectEqual(@as(?HapVariant, null), classify(fcc_hapa));
    try std.testing.expectEqual(@as(?HapVariant, null), classify(fcc_haphdr));
    try std.testing.expectEqual(@as(?HapVariant, null), classify(FourCC.initChars('X', 'X', 'X', 'X')));
}

test "isUnsupportedHapFourcc recognizes known unsupported variants" {
    try std.testing.expect(isUnsupportedHapFourcc(fcc_hapa));
    try std.testing.expect(isUnsupportedHapFourcc(fcc_haphdr));
    try std.testing.expect(!isUnsupportedHapFourcc(fcc_hap1));
    try std.testing.expect(!isUnsupportedHapFourcc(FourCC.initChars('X', 'X', 'X', 'X')));
}
