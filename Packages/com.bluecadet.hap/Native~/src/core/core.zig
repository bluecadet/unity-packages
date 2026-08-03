//! Root of the pure-Zig engine core.
//! No Unity dependency; only the vendored C libraries (snappy, minimp4)
//! compiled alongside this module (see build.zig). The C ABI the native
//! plugin exports lives one directory up, in src/bluecadet_hap.zig.

const std = @import("std");

pub const hap_frame = @import("hap_frame.zig");
pub const mmap_reader = @import("mmap_reader.zig");
pub const demuxer = @import("demuxer.zig");
pub const thread_pool = @import("thread_pool.zig");
pub const hap_decode = @import("hap_decode.zig");

test {
    _ = hap_frame;
    _ = mmap_reader;
    _ = demuxer;
    _ = @import("demuxer_test.zig");
    _ = thread_pool;
    _ = @import("sync.zig");
    _ = hap_decode;
    _ = @import("hap_decode_test.zig");
    _ = @import("fuzz_regressions_test.zig");
    _ = @import("demuxer_fuzz.zig");
    _ = @import("hap_decode_fuzz.zig");
}
