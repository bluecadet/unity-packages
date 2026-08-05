//! mmap_reader.zig
//!
//! Read-only memory-mapped file view. `init` opens and maps the file;
//! `deinit` unmaps it. An empty file is a valid, successful open whose
//! `data` is an empty slice; a missing/unreadable file, a failed `fstat`,
//! or a failed `mmap` are all reported as errors.
//!
//! Paging: the whole file is mapped, so every byte the decoder touches is a
//! page fault against the file. `prefetch` keeps those faults from
//! serializing into single-page reads on a cold page cache: it asks the
//! kernel to fault a specific byte range in ahead of use (the decode loop
//! points it at the next frame's sample). Pure hint: a failure costs
//! performance, never correctness, so it's swallowed.
//!
//! A whole-mapping sequential-access hint is issued at open time on Windows
//! only (`FILE_FLAG_SEQUENTIAL_SCAN`; see `initWindows`). POSIX deliberately
//! issues none: `madvise(MADV.SEQUENTIAL)` would make the kernel reclaim
//! pages behind the read point far more aggressively, which is fine for a
//! single front-to-back pass but punishes looping playback -- exactly this
//! decoder's normal access pattern -- by turning every loop-back into a full
//! re-fault of frame 0's pages. Per-frame `prefetch` (`MADV.WILLNEED`)
//! already covers readahead without that trade-off, so it's the only hint
//! POSIX gets.
//!
//! Design notes:
//!   * Failures are reported via a Zig error union.
//!   * `path` is only read during `init`; the mapping outlives it, so the
//!     caller is free to drop the string right after the call returns.
//!   * `MmapReader` is a plain struct; call `deinit` yourself when replacing
//!     or dropping one, since Zig has no destructors.

const std = @import("std");
const builtin = @import("builtin");
const posix = std.posix;

pub const MmapReader = struct {
    /// Read-only view of the mapped file contents. Empty for a zero-byte
    /// file, which is still a successful open.
    data: []const u8 = &.{},

    pub const InitError = error{
        OpenFailed,
        StatFailed,
        MmapFailed,
    };

    /// Open and memory-map the file at `path`.
    pub fn init(path: []const u8) InitError!MmapReader {
        return if (builtin.os.tag == .windows)
            initWindows(path)
        else
            initPosix(path);
    }

    /// Unmap the file, if mapped. Safe to call on a zero-value/empty reader.
    pub fn deinit(self: *MmapReader) void {
        if (self.data.len == 0) {
            self.* = .{};
            return;
        }

        if (builtin.os.tag == .windows) {
            _ = UnmapViewOfFile(@ptrCast(self.data.ptr));
        } else {
            const aligned: []align(std.heap.page_size_min) const u8 = @alignCast(self.data);
            posix.munmap(aligned);
        }

        self.* = .{};
    }

    /// Ask the kernel to fault `range` -- a sub-slice of `data` -- in ahead
    /// of the first dereference. Purely advisory: an unmapped or oversized
    /// range, or a kernel that refuses the hint, is a silent no-op, and the
    /// caller's next read faults the pages in the ordinary way.
    pub fn prefetch(self: *const MmapReader, range: []const u8) void {
        self.prefetchChecked(range) catch {};
    }

    /// Why a `prefetch` did nothing. Not public API -- `prefetch` swallows
    /// these -- but the suite asserts on them, so a hint that has silently
    /// stopped reaching the kernel fails a test instead of just getting
    /// slower.
    pub const PrefetchError = error{
        /// `range` is not contained by this reader's mapping.
        OutOfRange,
        /// The kernel rejected the hint.
        HintFailed,
    };

    fn prefetchChecked(self: *const MmapReader, range: []const u8) PrefetchError!void {
        if (range.len == 0 or range.len > self.data.len) return PrefetchError.OutOfRange;

        const map_start = @intFromPtr(self.data.ptr);
        const start = @intFromPtr(range.ptr);
        if (start < map_start or start - map_start > self.data.len - range.len) {
            return PrefetchError.OutOfRange;
        }

        // Both APIs below take page-granular ranges and quietly do nothing
        // (POSIX) or fail (Windows) otherwise, so widen to whole pages:
        // back to the page holding the first byte, forward past the page
        // holding the last. Rounding the end up cannot leave the mapping --
        // mmap/MapViewOfFile always map whole pages, so the mapping's last
        // page is fully addressable even when the file ends mid-page.
        const page = std.heap.pageSize();
        const aligned_start = std.mem.alignBackward(usize, start, page);
        const aligned_len = std.mem.alignForward(usize, start + range.len, page) - aligned_start;

        if (builtin.os.tag == .windows) {
            var entries: [1]MemoryRangeEntry = .{.{
                .VirtualAddress = @ptrFromInt(aligned_start),
                .NumberOfBytes = aligned_len,
            }};
            if (PrefetchVirtualMemory(GetCurrentProcess(), 1, &entries, 0) == 0) {
                return PrefetchError.HintFailed;
            }
        } else {
            const ptr: [*]align(std.heap.page_size_min) u8 = @ptrFromInt(aligned_start);
            posix.madvise(ptr, aligned_len, posix.MADV.WILLNEED) catch return PrefetchError.HintFailed;
        }
    }

    fn initPosix(path: []const u8) InitError!MmapReader {
        const fd = posix.openat(posix.AT.FDCWD, path, .{ .ACCMODE = .RDONLY }, 0) catch {
            return InitError.OpenFailed;
        };
        defer _ = std.c.close(fd);

        const size: usize = @intCast(fstatSize(fd) catch {
            return InitError.StatFailed;
        });

        if (size == 0) {
            return .{ .data = &.{} };
        }

        const mapped = posix.mmap(
            null,
            size,
            .{ .READ = true },
            .{ .TYPE = .SHARED },
            fd,
            0,
        ) catch {
            return InitError.MmapFailed;
        };

        return .{ .data = mapped };
    }

    /// Size in bytes of an already-open file descriptor, cross-platform.
    ///
    /// zig 0.16's `std.c` deliberately stubs `fstat`/`fstatat` out to `void`
    /// on Linux (see std/c.zig: `.linux => {}`) rather than exposing glibc's
    /// versioned fstat symbols, so the `std.c.fstat` call this used on macOS
    /// doesn't exist as a callable decl there. The blessed 0.16 replacement
    /// is `std.Io.File.stat`, but that requires an `Io` instance, and the
    /// only zero-setup one std ships (`std.Io.Threaded.global_single_threaded`)
    /// is documented as not supporting concurrency -- this codebase already
    /// hit a real bug from using it on a path that runs on real concurrent
    /// OS threads (see sync.zig's module doc), and `MmapReader.init` is
    /// exactly such a path: `hap_open` is called from the C# decode thread,
    /// one per open video. So Linux goes through a direct, dependency-free
    /// `statx(2)` syscall via
    /// `std.os.linux.statx` instead (fd-only, `AT.EMPTY_PATH`, `STATX.SIZE`),
    /// which needs neither libc nor the Io framework.
    fn fstatSize(fd: posix.fd_t) !u64 {
        if (builtin.os.tag == .linux) {
            const linux = std.os.linux;
            var stx: linux.Statx = undefined;
            const rc = linux.statx(fd, "", linux.AT.EMPTY_PATH, .{ .SIZE = true }, &stx);
            if (linux.errno(rc) != .SUCCESS) return error.StatFailed;
            return stx.size;
        }

        var st: std.c.Stat = undefined;
        if (posix.errno(std.c.fstat(fd, &st)) != .SUCCESS) return error.StatFailed;
        return @intCast(st.size);
    }

    fn initWindows(path: []const u8) InitError!MmapReader {
        const windows = std.os.windows;

        // ANSI CreateFileA because `path` is a narrow UTF-8 byte slice;
        // non-ASCII Windows paths are a known limitation. MAX_PATH (not
        // PATH_MAX_WIDE, which sizes the `\\?\`-prefixed wide-char API) is
        // the correct narrow-path limit for this ANSI call.
        var path_buf: [windows.MAX_PATH]u8 = undefined;
        if (path.len >= path_buf.len) return InitError.OpenFailed;
        @memcpy(path_buf[0..path.len], path);
        path_buf[path.len] = 0;
        const path_z: [*:0]const u8 = @ptrCast(&path_buf);

        const handle = CreateFileA(
            path_z,
            GENERIC_READ,
            FILE_SHARE_READ,
            null,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
            null,
        );
        if (handle == windows.INVALID_HANDLE_VALUE) return InitError.OpenFailed;
        defer _ = CloseHandle(handle);

        var file_size: i64 = undefined;
        if (GetFileSizeEx(handle, &file_size) == 0) return InitError.StatFailed;
        const size: usize = @intCast(file_size);

        if (size == 0) {
            return .{ .data = &.{} };
        }

        const mapping = CreateFileMappingA(handle, null, PAGE_READONLY, 0, 0, null);
        if (mapping == null) return InitError.MmapFailed;
        defer _ = CloseHandle(mapping);

        const view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
        if (view == null) return InitError.MmapFailed;

        const data_ptr: [*]const u8 = @ptrCast(view.?);
        return .{ .data = data_ptr[0..size] };
    }
};

// -- Windows-only extern bindings ------------------------------------------
//
// std.os.windows no longer wraps these Win32 file-API calls or exposes
// their flat constants (zig 0.16 keeps only typed wrappers like
// ACCESS_MASK), so both are declared directly here. Pruned at comptime on
// non-Windows targets; exercised by the cross-compiled Windows builds.

const GENERIC_READ: u32 = 0x8000_0000;
const FILE_SHARE_READ: u32 = 0x01;
const OPEN_EXISTING: u32 = 3;
const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;
/// Cache-manager hint that the file is read front to back. This flag
/// survives past `init` because it is set on the file object at
/// `CreateFileA` time and the mapping holds a reference to that object, not
/// to a descriptor that gets closed (unlike `F_RDAHEAD`/`POSIX_FADV_SEQUENTIAL`,
/// which are state on a POSIX file descriptor that `initPosix` closes as
/// soon as the mapping exists).
///
/// `initPosix` does have a mapping-wide equivalent available --
/// `madvise(MADV.SEQUENTIAL)`, which needs no fd -- but deliberately doesn't
/// call it: it would make the kernel reclaim pages behind the read point far
/// more aggressively, which punishes this decoder's normal looping-playback
/// access pattern (see the module doc). Per-frame `prefetch` covers
/// readahead on POSIX instead.
const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
const PAGE_READONLY: u32 = 0x02;
const FILE_MAP_READ: u32 = 0x0004;

/// WIN32_MEMORY_RANGE_ENTRY: one address range for PrefetchVirtualMemory.
const MemoryRangeEntry = extern struct {
    VirtualAddress: ?*anyopaque,
    NumberOfBytes: usize,
};

extern "kernel32" fn CreateFileA(
    lpFileName: [*:0]const u8,
    dwDesiredAccess: u32,
    dwShareMode: u32,
    lpSecurityAttributes: ?*anyopaque,
    dwCreationDisposition: u32,
    dwFlagsAndAttributes: u32,
    hTemplateFile: ?*anyopaque,
) callconv(.winapi) ?*anyopaque;

extern "kernel32" fn GetFileSizeEx(
    hFile: ?*anyopaque,
    lpFileSize: *i64,
) callconv(.winapi) c_int;

extern "kernel32" fn CreateFileMappingA(
    hFile: ?*anyopaque,
    lpFileMappingAttributes: ?*anyopaque,
    flProtect: u32,
    dwMaximumSizeHigh: u32,
    dwMaximumSizeLow: u32,
    lpName: ?*anyopaque,
) callconv(.winapi) ?*anyopaque;

extern "kernel32" fn MapViewOfFile(
    hFileMappingObject: ?*anyopaque,
    dwDesiredAccess: u32,
    dwFileOffsetHigh: u32,
    dwFileOffsetLow: u32,
    dwNumberOfBytesToMap: usize,
) callconv(.winapi) ?*anyopaque;

extern "kernel32" fn UnmapViewOfFile(
    lpBaseAddress: ?*const anyopaque,
) callconv(.winapi) c_int;

extern "kernel32" fn CloseHandle(
    hObject: ?*anyopaque,
) callconv(.winapi) c_int;

extern "kernel32" fn GetCurrentProcess() callconv(.winapi) ?*anyopaque;

/// Windows 8 / Server 2012 and newer; the oldest Windows the Unity versions
/// this package supports run on, so no runtime GetProcAddress dance.
extern "kernel32" fn PrefetchVirtualMemory(
    hProcess: ?*anyopaque,
    NumberOfEntries: usize,
    VirtualAddresses: [*]MemoryRangeEntry,
    Flags: u32,
) callconv(.winapi) c_int;

const repo_root_hap1_mov = "tests/fixtures/hap1.mov";

test "init maps a real fixture file and reads its MP4 box header" {
    var reader = try MmapReader.init(repo_root_hap1_mov);
    defer reader.deinit();

    try std.testing.expect(reader.data.len > 0);
    // ISO base media file format: a 4-byte big-endian box size followed by a
    // 4-byte box type. The first box in a .mov is typically "ftyp".
    try std.testing.expectEqualSlices(u8, "ftyp", reader.data[4..8]);
}

test "init fails for a nonexistent file" {
    const result = MmapReader.init("tests/fixtures/does_not_exist.mov");
    try std.testing.expectError(MmapReader.InitError.OpenFailed, result);
}

test "prefetch of a mapped range reaches the kernel" {
    var reader = try MmapReader.init(repo_root_hap1_mov);
    defer reader.deinit();

    // The checked form is what the assertion is for: `prefetch` itself
    // reports nothing, so only this can tell "the kernel took the hint"
    // apart from "the call has quietly stopped being made".
    try reader.prefetchChecked(reader.data[0..1]);

    // An unaligned, mid-file range is the shape the decode path passes.
    const middle = reader.data.len / 2;
    try reader.prefetchChecked(reader.data[middle - 1 ..][0..3]);

    // The last byte: the aligned length runs past end-of-file into the
    // mapping's final page, which must still be a legal range to hint.
    try reader.prefetchChecked(reader.data[reader.data.len - 1 ..]);
}

test "prefetch rejects ranges outside the mapping" {
    var reader = try MmapReader.init(repo_root_hap1_mov);
    defer reader.deinit();

    var stack_byte: [1]u8 = .{0};
    try std.testing.expectError(
        MmapReader.PrefetchError.OutOfRange,
        reader.prefetchChecked(&stack_byte),
    );
    try std.testing.expectError(
        MmapReader.PrefetchError.OutOfRange,
        reader.prefetchChecked(reader.data[0..0]),
    );

    var empty: MmapReader = .{};
    try std.testing.expectError(
        MmapReader.PrefetchError.OutOfRange,
        empty.prefetchChecked(reader.data[0..1]),
    );
    empty.prefetch(reader.data[0..1]); // swallowed, no crash
}

test "deinit on a zero-value reader is a no-op" {
    var reader: MmapReader = .{};
    reader.deinit();
    try std.testing.expectEqual(@as(usize, 0), reader.data.len);
}
