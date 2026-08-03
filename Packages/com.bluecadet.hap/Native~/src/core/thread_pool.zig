//! thread_pool.zig
//!
//! The "inner" thread pool used for parallel chunk decode of a single
//! multi-chunk ("Complex" compressor) Hap texture: hap_decode.zig calls
//! `InnerThreadPool.instance().execute()` directly with a work function to
//! call once per chunk index, and `execute` returns only once every index
//! has been dispatched and completed.
//!
//! Sizing: max(1, hardware_concurrency - kOuterWorkers), clamped to a
//! minimum of 1. `kOuterWorkers` covers the threads the host engine runs
//! outside this pool (Unity's main/render thread plus the C# decode thread
//! that drives decode). The host can override how many of the pool's workers
//! actually participate in a batch via `setActiveWorkerCount` (the C ABI's
//! `hap_set_thread_count`), which takes effect on the next `execute`.
//!
//! Singleton: process-wide, shared by every open video. Each open video has
//! its own C# decode thread, so several of them can call execute() at the
//! same time; execute() serializes those calls (dispatch_mutex) so the
//! shared work-batch state is never touched by two callers at once. This
//! trades chunk-level parallelism across simultaneously-decoding videos for
//! correctness -- the thread count stays bounded either way.
//!
//! Mutex/Condition come from sync.zig (see that module's docs for the full
//! Zig-0.16 rationale for wrapping native OS primitives instead of the
//! removed std.Thread.Mutex/Condition).

const std = @import("std");

const sync = @import("sync.zig");

const Mutex = sync.Mutex;
const Condition = sync.Condition;

/// Per-chunk work function signature: called once per chunk index by
/// `execute` (see below), on whichever thread -- pool worker or the calling
/// thread itself -- that index was partitioned to.
pub const HapDecodeWorkFunction = *const fn (p: ?*anyopaque, index: c_uint) void;

/// Threads assumed to be running outside this pool when sizing the shared
/// singleton against the hardware: the host engine's main/render thread and
/// the thread it drives decode from (which also decodes one partition
/// itself inside `execute`).
const kOuterWorkers: u32 = 2;

/// Per-worker partition: start index (inclusive) and end index (exclusive).
const Partition = struct {
    start: u32,
    end: u32,
};

/// A thread pool for parallel chunk decode within a single frame.
///
/// See the module docs for the dispatch contract and sizing rules.
pub const InnerThreadPool = struct {
    allocator: std.mem.Allocator,

    /// Worker threads (fixed-size, sized at create() time).
    workers: []std.Thread,

    /// Number of worker threads (pool size, excluding the calling thread).
    num_workers: u32,

    /// How many of those workers take a share of the next batch, clamped to
    /// `num_workers` when read. Lets the host retune decode parallelism at
    /// runtime without respawning threads (see `setActiveWorkerCount`); the
    /// idle workers still wake per batch but get an empty partition. At zero
    /// -- "no helper threads" -- `execute` skips the pool entirely rather
    /// than waking every worker to read an empty partition.
    active_workers: std.atomic.Value(u32) = .init(std.math.maxInt(u32)),

    /// Serializes execute() calls across concurrent outer-pool workers.
    /// Held for the full duration of one batch's dispatch-and-wait.
    dispatch_mutex: Mutex = .{},

    /// Synchronization.
    mutex: Mutex = .{},
    cv_start: Condition = .{},
    cv_done: Condition = .{},

    /// Shared work state, set by execute() before waking workers. Guarded
    /// by `mutex`.
    func: ?HapDecodeWorkFunction = null,
    p: ?*anyopaque = null,
    remaining: u32 = 0,

    /// Monotonically increasing batch counter. Workers track their last
    /// seen batch and only proceed when the counter changes, preventing
    /// re-entry within the same batch. Guarded by `mutex` -- every read and
    /// write happens with `mutex` held, so this is plain state, not atomic.
    work_batch: u32 = 0,

    /// Per-worker partition: index `num_workers` (the last slot) is the
    /// calling thread's own share.
    partitions: []Partition,

    /// Pool lifecycle flag. Guarded by `mutex`.
    running: bool = true,

    /// Create a pool with the given number of worker threads (clamped to a
    /// minimum of 1). Returns a heap-allocated pool (held by pointer): the
    /// worker threads capture the pool's address, so it must live at a
    /// stable location -- callers must call `destroy` exactly once, after
    /// which the pointer is invalid.
    pub fn create(allocator: std.mem.Allocator, requested_workers: u32) !*InnerThreadPool {
        const n = @max(@as(u32, 1), requested_workers);

        const self = try allocator.create(InnerThreadPool);
        errdefer allocator.destroy(self);

        const partitions = try allocator.alloc(Partition, n + 1); // +1 for the calling thread
        errdefer allocator.free(partitions);

        const workers = try allocator.alloc(std.Thread, n);
        errdefer allocator.free(workers);

        self.* = .{
            .allocator = allocator,
            .workers = workers,
            .num_workers = n,
            .partitions = partitions,
        };

        // Worker-spawn failure is unrecoverable -- the pool's purpose is
        // these threads -- so panic rather than degrade.
        for (0..n) |i| {
            workers[i] = std.Thread.spawn(.{}, workerLoop, .{ self, @as(u32, @intCast(i)) }) catch
                @panic("InnerThreadPool: failed to spawn worker thread");
        }

        return self;
    }

    /// Stop and join all worker threads, then free the pool. Invalidates
    /// the pointer returned by `create`.
    pub fn destroy(self: *InnerThreadPool) void {
        // Signal shutdown under the lock, then wake every worker so each one
        // observes `running == false` and returns from its wait.
        self.mutex.lock();
        self.running = false;
        self.mutex.unlock();
        self.cv_start.notifyAll();
        for (self.workers) |w| w.join();

        self.cv_done.deinit();
        self.cv_start.deinit();
        self.mutex.deinit();
        self.dispatch_mutex.deinit();

        const allocator = self.allocator;
        allocator.free(self.workers);
        allocator.free(self.partitions);
        allocator.destroy(self);
    }

    /// Number of worker threads in the pool (excluding the calling thread).
    pub fn workerCount(self: *const InnerThreadPool) u32 {
        return self.num_workers;
    }

    /// Number of workers that will take a share of the next batch (never
    /// more than `workerCount`). Excludes the calling thread, which always
    /// decodes a share of its own.
    pub fn activeWorkerCount(self: *const InnerThreadPool) u32 {
        return @min(self.active_workers.load(.monotonic), self.num_workers);
    }

    /// Cap how many workers take a share of each batch. Safe to call from
    /// any thread at any time; it takes effect on the next `execute` (a
    /// batch already in flight keeps the partitioning it started with).
    pub fn setActiveWorkerCount(self: *InnerThreadPool, workers: u32) void {
        self.active_workers.store(workers, .monotonic);
    }

    /// Execute `count` work items across the thread pool. Blocks until all
    /// items complete. Safe to call concurrently from multiple outer-pool
    /// workers -- calls are internally serialized.
    pub fn execute(self: *InnerThreadPool, func: HapDecodeWorkFunction, p: ?*anyopaque, count: u32) void {
        if (count <= 1) {
            func(p, 0);
            return;
        }

        // Read once, outside dispatch_mutex: that mutex guards the shared
        // batch state, not `active_workers`, which is atomic and documented
        // as taking effect "on the next execute" -- so a concurrent
        // setActiveWorkerCount either lands before this read or applies to a
        // later batch, exactly as it would have when read below.
        const active = self.activeWorkerCount();

        // No helper threads: every index is the calling thread's share, so
        // run them here. Going through the pool would wake all `num_workers`
        // threads, have each read an empty partition and signal done, and
        // block the caller meanwhile -- the same work in the same order, at
        // the cost of a full wakeup round trip per chunked frame.
        if (active == 0) {
            var i: u32 = 0;
            while (i < count) : (i += 1) func(p, i);
            return;
        }

        self.dispatch_mutex.lock();
        defer self.dispatch_mutex.unlock();

        // Only the active workers take a share; every other slot is left
        // empty so a woken-but-idle worker has nothing to do (see
        // setActiveWorkerCount).
        const total_workers = active + 1; // calling thread + active workers

        @memset(self.partitions, Partition{ .start = 0, .end = 0 });

        const base = count / total_workers;
        const remainder = count % total_workers;
        var pos: u32 = 0;
        for (0..total_workers) |i| {
            const extra: u32 = if (i < remainder) 1 else 0;
            const size = base + extra;
            // The last share is the calling thread's, which always uses the
            // final slot (index `num_workers`).
            const slot = if (i < active) i else self.num_workers;
            self.partitions[slot] = .{ .start = pos, .end = pos + size };
            pos += size;
        }

        {
            self.mutex.lock();
            self.func = func;
            self.p = p;
            self.remaining = self.num_workers;
            self.work_batch += 1;
            self.mutex.unlock();
        }
        self.cv_start.notifyAll();

        const my_part = self.partitions[self.num_workers];
        var i = my_part.start;
        while (i < my_part.end) : (i += 1) {
            func(p, i);
        }

        {
            self.mutex.lock();
            while (self.remaining != 0) self.cv_done.wait(&self.mutex);
            self.func = null;
            self.p = null;
            self.mutex.unlock();
        }
    }

    /// Worker thread entry point.
    fn workerLoop(self: *InnerThreadPool, worker_id: u32) void {
        var my_batch: u32 = 0; // last batch this worker processed

        while (true) {
            self.mutex.lock();
            while (self.running and self.work_batch == my_batch) {
                self.cv_start.wait(&self.mutex);
            }
            if (!self.running) {
                self.mutex.unlock();
                return;
            }

            my_batch = self.work_batch;

            const part = self.partitions[worker_id];
            const func = self.func.?;
            const p = self.p;

            self.mutex.unlock();

            var i = part.start;
            while (i < part.end) : (i += 1) {
                func(p, i);
            }

            self.mutex.lock();
            self.remaining -= 1;
            self.mutex.unlock();
            self.cv_done.notifyOne();
        }
    }
};

/// Sizing formula for the shared singleton -- see the module docs.
fn singletonWorkerCount() u32 {
    const hw: u32 = @intCast(std.Thread.getCpuCount() catch 1);
    var num_workers: u32 = if (hw <= kOuterWorkers) 1 else hw - kOuterWorkers;
    if (num_workers < 1) num_workers = 1;
    return num_workers;
}

var singleton_mutex: Mutex = .{};
var singleton: std.atomic.Value(?*InnerThreadPool) = .init(null);

/// Access the shared instance, sized from the hardware per the module docs'
/// formula. Created on first access and intentionally never torn down: it is
/// process-wide state with no owner, and the worker threads would otherwise
/// have to be respawned every time the last video closed.
pub fn instance() *InnerThreadPool {
    // Fast path: no lock once the pool exists, since every chunked frame
    // decode goes through here.
    if (singleton.load(.acquire)) |p| return p;

    singleton_mutex.lock();
    defer singleton_mutex.unlock();
    // Re-check under the lock: another thread may have created the pool
    // between the load above and this lock being acquired.
    if (singleton.load(.monotonic)) |p| return p;

    const pool = InnerThreadPool.create(std.heap.page_allocator, singletonWorkerCount()) catch
        @panic("InnerThreadPool: singleton init failed");
    singleton.store(pool, .release);
    return pool;
}

// -----------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------

const testing = std.testing;

test "InnerThreadPool.create/destroy round-trips cleanly" {
    const pool = try InnerThreadPool.create(testing.allocator, 2);
    try testing.expectEqual(@as(u32, 2), pool.workerCount());
    pool.destroy();
}

test "InnerThreadPool.create clamps a zero worker count to 1" {
    const pool = try InnerThreadPool.create(testing.allocator, 0);
    try testing.expectEqual(@as(u32, 1), pool.workerCount());
    pool.destroy();
}

test "InnerThreadPool.execute calls the work function directly for count <= 1" {
    const pool = try InnerThreadPool.create(testing.allocator, 2);
    defer pool.destroy();

    var seen: u32 = 0;

    const Ctx = struct {
        fn work(p: ?*anyopaque, index: c_uint) void {
            const counter: *u32 = @ptrCast(@alignCast(p.?));
            counter.* += 1;
            try_expect_zero_index(index);
        }
        fn try_expect_zero_index(index: c_uint) void {
            std.debug.assert(index == 0);
        }
    };

    pool.execute(Ctx.work, &seen, 1);
    try testing.expectEqual(@as(u32, 1), seen);

    pool.execute(Ctx.work, &seen, 0);
    try testing.expectEqual(@as(u32, 2), seen);
}

test "InnerThreadPool.execute dispatches every index exactly once across workers" {
    const pool = try InnerThreadPool.create(testing.allocator, 3);
    defer pool.destroy();

    const count: u32 = 37;
    var seen = [_]std.atomic.Value(u32){std.atomic.Value(u32).init(0)} ** count;

    const Ctx = struct {
        fn work(p: ?*anyopaque, index: c_uint) void {
            const arr: [*]std.atomic.Value(u32) = @ptrCast(@alignCast(p.?));
            _ = arr[index].fetchAdd(1, .monotonic);
        }
    };

    pool.execute(Ctx.work, &seen, count);

    for (&seen) |*v| {
        try testing.expectEqual(@as(u32, 1), v.load(.monotonic));
    }
}

test "InnerThreadPool.execute can be called repeatedly (batch counter advances)" {
    const pool = try InnerThreadPool.create(testing.allocator, 2);
    defer pool.destroy();

    const count: u32 = 10;
    var totals = [_]std.atomic.Value(u32){std.atomic.Value(u32).init(0)} ** count;

    const Ctx = struct {
        fn work(p: ?*anyopaque, index: c_uint) void {
            const arr: [*]std.atomic.Value(u32) = @ptrCast(@alignCast(p.?));
            _ = arr[index].fetchAdd(1, .monotonic);
        }
    };

    var round: u32 = 0;
    while (round < 5) : (round += 1) {
        pool.execute(Ctx.work, &totals, count);
    }

    for (&totals) |*v| {
        try testing.expectEqual(@as(u32, 5), v.load(.monotonic));
    }
}

test "InnerThreadPool.setActiveWorkerCount still dispatches every index exactly once" {
    const pool = try InnerThreadPool.create(testing.allocator, 3);
    defer pool.destroy();

    const count: u32 = 29;
    var seen = [_]std.atomic.Value(u32){std.atomic.Value(u32).init(0)} ** count;

    const Ctx = struct {
        fn work(p: ?*anyopaque, index: c_uint) void {
            const arr: [*]std.atomic.Value(u32) = @ptrCast(@alignCast(p.?));
            _ = arr[index].fetchAdd(1, .monotonic);
        }
    };

    // Calling thread only (no workers), then one worker, then more workers
    // than exist (clamped back to the pool size).
    for ([_]u32{ 0, 1, 99 }) |active| {
        pool.setActiveWorkerCount(active);
        try testing.expectEqual(@min(active, pool.workerCount()), pool.activeWorkerCount());
        pool.execute(Ctx.work, &seen, count);
    }

    for (&seen) |*v| {
        try testing.expectEqual(@as(u32, 3), v.load(.monotonic));
    }
}

test "InnerThreadPool singleton instance() is reachable and sized at least 1" {
    const pool = instance();
    try testing.expect(pool.workerCount() >= 1);
}
