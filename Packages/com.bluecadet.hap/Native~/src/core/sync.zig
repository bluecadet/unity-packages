//! sync.zig — Mutex/Condition wrapper for real OS-thread synchronization,
//! used here by thread_pool.zig. Ported as-is from the godot-hap-video core,
//! where several threaded modules share it.
//!
//! Zig 0.16 note: `std.Thread.Mutex`/`std.Thread.Condition` were removed
//! upstream with no direct OS-thread-safe replacement in std
//! (`std.Io.Mutex`/`Io.Condition` require threading an `Io` instance
//! through every call, and the only zero-setup instance std ships,
//! `std.Io.Threaded.global_single_threaded`, is documented as not
//! supporting concurrency at all -- an earlier version of this codebase
//! backed `Mutex`/`Condition` with it anyway, which produced an
//! intermittent shutdown race under real cross-thread contention).
//! `Mutex`/`Condition` below wrap the native OS primitives behind the old
//! infallible, io-free call shape instead -- POSIX
//! `pthread_mutex_t`/`pthread_cond_t` (via `std.c`) on macOS/Linux,
//! SRWLOCK/CONDITION_VARIABLE (via kernel32) on Windows -- which are
//! genuinely safe for real OS threads.

const std = @import("std");
const builtin = @import("builtin");
const c = std.c;
const windows = std.os.windows;

const is_windows = builtin.target.os.tag == .windows;

/// Drop-in replacement for the removed std.Thread.Mutex (see module docs).
pub const Mutex = if (is_windows) WindowsMutex else PosixMutex;

/// Drop-in replacement for the removed std.Thread.Condition (see module
/// docs).
pub const Condition = if (is_windows) WindowsCondition else PosixCondition;

/// Backed directly by a POSIX pthread_mutex_t.
const PosixMutex = struct {
    inner: c.pthread_mutex_t = .{},

    pub fn lock(m: *Mutex) void {
        const rc = c.pthread_mutex_lock(&m.inner);
        std.debug.assert(rc == .SUCCESS);
    }

    pub fn unlock(m: *Mutex) void {
        const rc = c.pthread_mutex_unlock(&m.inner);
        std.debug.assert(rc == .SUCCESS);
    }

    /// Releases OS resources held by the mutex. Only needed for
    /// heap-allocated pools that are actually torn down (tests); the
    /// process-lifetime singletons are intentionally never torn down.
    pub fn deinit(m: *Mutex) void {
        _ = c.pthread_mutex_destroy(&m.inner);
    }
};

/// Backed directly by a POSIX pthread_cond_t.
const PosixCondition = struct {
    inner: c.pthread_cond_t = .{},

    pub fn wait(cv: *Condition, mu: *Mutex) void {
        const rc = c.pthread_cond_wait(&cv.inner, &mu.inner);
        std.debug.assert(rc == .SUCCESS);
    }

    pub fn notifyOne(cv: *Condition) void {
        const rc = c.pthread_cond_signal(&cv.inner);
        std.debug.assert(rc == .SUCCESS);
    }

    pub fn notifyAll(cv: *Condition) void {
        const rc = c.pthread_cond_broadcast(&cv.inner);
        std.debug.assert(rc == .SUCCESS);
    }

    /// See Mutex.deinit.
    pub fn deinit(cv: *Condition) void {
        _ = c.pthread_cond_destroy(&cv.inner);
    }
};

/// Backed directly by a Win32 SRWLOCK (exclusive mode only, matching the
/// POSIX backend's plain-mutex semantics).
const WindowsMutex = struct {
    inner: windows.SRWLOCK = .{},

    pub fn lock(m: *Mutex) void {
        AcquireSRWLockExclusive(&m.inner);
    }

    pub fn unlock(m: *Mutex) void {
        ReleaseSRWLockExclusive(&m.inner);
    }

    /// SRWLOCKs hold no OS resources; present for API parity with the
    /// POSIX backend (see PosixMutex.deinit).
    pub fn deinit(m: *Mutex) void {
        _ = m;
    }
};

/// Backed directly by a Win32 CONDITION_VARIABLE.
const WindowsCondition = struct {
    inner: windows.CONDITION_VARIABLE = .{},

    pub fn wait(cv: *Condition, mu: *Mutex) void {
        // INFINITE timeout; flags 0 = the SRWLOCK is held in exclusive
        // mode. With INFINITE the call can only fail on API misuse.
        const INFINITE: u32 = 0xFFFF_FFFF;
        std.debug.assert(SleepConditionVariableSRW(&cv.inner, &mu.inner, INFINITE, 0) != 0);
    }

    pub fn notifyOne(cv: *Condition) void {
        WakeConditionVariable(&cv.inner);
    }

    pub fn notifyAll(cv: *Condition) void {
        WakeAllConditionVariable(&cv.inner);
    }

    /// CONDITION_VARIABLEs hold no OS resources; see WindowsMutex.deinit.
    pub fn deinit(cv: *Condition) void {
        _ = cv;
    }
};

// -- Windows-only extern bindings ------------------------------------------
//
// std.os.windows declares the SRWLOCK/CONDITION_VARIABLE types but no
// longer wraps these kernel32 entry points, so they are declared directly
// here (same pattern as mmap_reader.zig's file-mapping bindings). Pruned
// at comptime on non-Windows targets.

extern "kernel32" fn AcquireSRWLockExclusive(srw_lock: *windows.SRWLOCK) callconv(.winapi) void;
extern "kernel32" fn ReleaseSRWLockExclusive(srw_lock: *windows.SRWLOCK) callconv(.winapi) void;
extern "kernel32" fn SleepConditionVariableSRW(
    condition_variable: *windows.CONDITION_VARIABLE,
    srw_lock: *windows.SRWLOCK,
    milliseconds: u32,
    flags: u32,
) callconv(.winapi) c_int;
extern "kernel32" fn WakeConditionVariable(condition_variable: *windows.CONDITION_VARIABLE) callconv(.winapi) void;
extern "kernel32" fn WakeAllConditionVariable(condition_variable: *windows.CONDITION_VARIABLE) callconv(.winapi) void;

// -----------------------------------------------------------------------
// Tests
//
// These primitives are otherwise only covered transitively, through
// thread_pool.zig -- and the bug they were written to fix (the shutdown
// race noted above) is exactly the kind that a wrapper can reintroduce
// while every higher-level test still passes. So: real OS threads, and a
// lost wakeup hangs the suite rather than passing quietly.
// -----------------------------------------------------------------------

const testing = std.testing;

test "Mutex serializes contending threads" {
    var m: Mutex = .{};
    defer m.deinit();

    // Uncontended round-trip first: a lock that never unlocks deadlocks the
    // second lock() below instead of failing here.
    m.lock();
    m.unlock();

    // Two threads incrementing a plain (non-atomic) counter under the lock:
    // a mutex that doesn't actually exclude loses updates.
    const Counter = struct {
        m: *Mutex,
        value: u64 = 0,

        fn bump(self: *@This()) void {
            for (0..20_000) |_| {
                self.m.lock();
                defer self.m.unlock();
                self.value += 1;
            }
        }
    };

    var counter: Counter = .{ .m = &m };
    const a = try std.Thread.spawn(.{}, Counter.bump, .{&counter});
    const b = try std.Thread.spawn(.{}, Counter.bump, .{&counter});
    a.join();
    b.join();

    try testing.expectEqual(@as(u64, 40_000), counter.value);
}

/// A predicate plus the mutex/condition guarding it: the shape thread_pool
/// uses for every handoff.
const Gate = struct {
    m: Mutex = .{},
    cv: Condition = .{},
    open: bool = false,
    passed: u32 = 0,

    fn deinit(self: *Gate) void {
        self.m.deinit();
        self.cv.deinit();
    }

    fn waitAndPass(self: *Gate) void {
        self.m.lock();
        defer self.m.unlock();
        while (!self.open) self.cv.wait(&self.m);
        self.passed += 1;
    }
};

test "Condition.notifyOne wakes a waiting thread" {
    var gate: Gate = .{};
    defer gate.deinit();

    const t = try std.Thread.spawn(.{}, Gate.waitAndPass, .{&gate});

    gate.m.lock();
    gate.open = true;
    gate.cv.notifyOne();
    gate.m.unlock();

    t.join(); // a lost wakeup hangs here rather than reporting a failure
    try testing.expectEqual(@as(u32, 1), gate.passed);
}

test "Condition.notifyAll releases every waiter" {
    var gate: Gate = .{};
    defer gate.deinit();

    var threads: [4]std.Thread = undefined;
    for (&threads) |*t| t.* = try std.Thread.spawn(.{}, Gate.waitAndPass, .{&gate});

    gate.m.lock();
    gate.open = true;
    gate.cv.notifyAll();
    gate.m.unlock();

    for (threads) |t| t.join();
    try testing.expectEqual(@as(u32, threads.len), gate.passed);
}
