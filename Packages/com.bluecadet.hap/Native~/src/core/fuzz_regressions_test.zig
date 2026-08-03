//! fuzz_regressions_test.zig — replays fixed fuzzer-found crash inputs.
//!
//! Each fixture under tests/fixtures/fuzz_regressions/ is a raw input
//! that previously crashed, hung, leaked, or OOM'd Demuxer.open(); the
//! bugs are fixed and this replays the exact inputs deterministically, so
//! a regression shows up as an ordinary crash/leak/hang in this suite
//! instead of only in an occasional local fuzz run.
//!
//! The corpus itself is enumerated by test_support.RegressionCorpus, which
//! walks the directory once for this suite and both fuzz harnesses.

const std = @import("std");
const testing = std.testing;

const mmap_reader = @import("mmap_reader.zig");
const demuxer_mod = @import("demuxer.zig");
const test_support = @import("test_support.zig");

const MmapReader = mmap_reader.MmapReader;
const Demuxer = demuxer_mod.Demuxer;

/// Fuzz-found inputs are raw bytes, not valid Hap MOVs in most cases --
/// the only thing under test is that open() returns normally (no
/// crash/leak/hang), not that it succeeds.
fn replay(data: []const u8) !void {
    var reader: MmapReader = .{ .data = data };

    var dem: Demuxer = .{};
    defer dem.deinit(testing.allocator);
    dem.open(testing.allocator, &reader) catch {};
}

test "fuzz regressions replay without crash, leak, or hang" {
    var corpus = try test_support.RegressionCorpus.open(testing.allocator);
    defer corpus.deinit(testing.allocator);

    if (!corpus.present) return error.SkipZigTest; // no fuzz_regressions fixtures found

    for (corpus.entries.items) |data| try replay(data);

    // Guard against a typo'd path silently turning this into a no-op test.
    try testing.expect(corpus.entries.items.len > 0);
}
