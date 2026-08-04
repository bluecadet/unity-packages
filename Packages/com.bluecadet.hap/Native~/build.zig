//! Build for the `bluecadet_hap` Unity native plugin.
//!
//!   zig build test                                     -- core + C ABI tests
//!   zig build all -p ../Plugins -Doptimize=ReleaseFast -- every shipped target
//!
//! `all` writes Plugins/<os>-<arch>/bluecadet_hap.{bundle,dll,so}; those
//! names and directories are referenced by Unity .meta files, so they must
//! not change. Run both from this directory: the test fixtures are looked up
//! relative to it.

const std = @import("std");
const builtin = @import("builtin");
const Build = std.Build;

const supported_targets = [_]TargetSpec{
    .{
        .name = "macos-arm64",
        .query = .{ .cpu_arch = .aarch64, .os_tag = .macos },
    },
    .{
        .name = "macos-x86_64",
        .query = .{ .cpu_arch = .x86_64, .os_tag = .macos },
    },
    .{
        .name = "windows-arm64",
        .query = .{ .cpu_arch = .aarch64, .os_tag = .windows, .abi = .gnu },
    },
    .{
        .name = "windows-x86_64",
        .query = .{ .cpu_arch = .x86_64, .os_tag = .windows, .abi = .gnu },
    },
    .{
        .name = "linux-arm64",
        .query = .{ .cpu_arch = .aarch64, .os_tag = .linux, .abi = .gnu },
    },
    .{
        .name = "linux-x86_64",
        .query = .{ .cpu_arch = .x86_64, .os_tag = .linux, .abi = .gnu },
    },
};

const TargetSpec = struct {
    name: []const u8,
    query: std.Target.Query,
};

const common_warn_flags = [_][]const u8{
    "-Wall",
    "-Wextra",
    "-Wno-unused-parameter",
    // Only this plugin's own `hap_*` entry points belong in the dynamic
    // symbol table; a Unity process can host several plugins, and an
    // exported `snappy_uncompress` would be up for grabs between them.
    "-fvisibility=hidden",
};

const snappy_flags = common_warn_flags ++ [_][]const u8{ "-std=c++17", "-DHAVE_CONFIG_H=1" };

/// Snappy has no runtime CPU dispatch: SSSE3/BMI2 decode is purely
/// compile-time gated, so it must be paired with the matching codegen flag.
/// Scoped to x86_64 only; aarch64 already gets NEON for free via __ARM_NEON.
const snappy_x86_flags = snappy_flags ++ [_][]const u8{ "-mssse3", "-mbmi2" };

/// Wires up the vendored C/C++ (minimp4, snappy) that the Zig core wraps
/// with hand-written `extern fn` declarations. Shared by the shipped library
/// module and the test module.
fn addCSources(b: *Build, mod: *Build.Module, target: Build.ResolvedTarget) void {
    mod.addIncludePath(b.path("vendor/minimp4"));
    mod.addIncludePath(b.path("vendor/snappy"));
    mod.addIncludePath(b.path("vendor/snappy/snappy_config"));

    mod.addCSourceFiles(.{
        .files = &.{
            "vendor/minimp4/minimp4.c",
            "src/core/minimp4_shim.c",
        },
        .flags = &common_warn_flags,
    });

    mod.addCSourceFiles(.{
        .files = &.{
            "vendor/snappy/snappy.cc",
            "vendor/snappy/snappy-c.cc",
            "vendor/snappy/snappy-sinksource.cc",
            "vendor/snappy/snappy-stubs-internal.cc",
        },
        .flags = switch (target.result.cpu.arch) {
            .x86_64 => &snappy_x86_flags,
            else => &snappy_flags,
        },
    });
}

/// The host, spelled as an explicit target query rather than "native".
///
/// zig 0.16 cannot build its bundled libc++ against a *native* macOS target
/// on current SDKs (libcxx's own random.cpp fails on an undeclared
/// `INFINITY` coming out of the SDK's math.h), and snappy needs libc++.
/// Naming the host explicitly makes zig use its own bundled platform headers
/// instead of the installed SDK: the same machine code, minus host-CPU
/// feature detection, which nothing here depends on (the only CPU-specific
/// flags are snappy's, and those are selected per architecture).
fn hostQuery() std.Target.Query {
    return .{ .cpu_arch = builtin.cpu.arch, .os_tag = builtin.os.tag };
}

pub fn build(b: *Build) void {
    const requested_target = b.standardTargetOptions(.{});
    const target = if (requested_target.query.isNative())
        b.resolveTargetQuery(hostQuery())
    else
        requested_target;
    const optimize = b.option(
        std.builtin.OptimizeMode,
        "optimize",
        "Prioritize performance, safety, or binary size (default ReleaseFast)",
    ) orelse .ReleaseFast;
    const test_optimize = b.option(
        std.builtin.OptimizeMode,
        "test-optimize",
        "Optimization mode for the test suite (default Debug, for runtime safety checks)",
    ) orelse .Debug;

    // --- Default target: install the host build next to the cross builds. ---
    const default_lib = addHapPlugin(b, target, optimize);
    const default_dest = b.fmt("{s}/{s}", .{
        targetDirectoryName(b, target.result),
        artifactFileName(target.result),
    });
    const default_install = b.addInstallFile(default_lib.getEmittedBin(), default_dest);
    b.getInstallStep().dependOn(&default_install.step);

    // --- `all`: every target Unity ships. ---
    const all_step = b.step("all", "Build every supported Unity native plugin target");
    for (supported_targets) |spec| {
        const resolved = b.resolveTargetQuery(spec.query);
        const lib = addHapPlugin(b, resolved, optimize);
        const dest = b.fmt("{s}/{s}", .{ spec.name, artifactFileName(resolved.result) });
        const install = b.addInstallFile(lib.getEmittedBin(), dest);
        all_step.dependOn(&install.step);
    }

    // --- `test`: the core suite plus the exported C ABI's own tests. ---
    //
    // The tests default to Debug (runtime safety on) independently of the
    // shipped build's optimization mode.
    const test_mod = b.createModule(.{
        .root_source_file = b.path("src/bluecadet_hap.zig"),
        .target = target,
        .optimize = test_optimize,
        .link_libc = true,
        .link_libcpp = true,
    });
    addCSources(b, test_mod, target);

    const tests = b.addTest(.{ .root_module = test_mod });
    const run_tests = b.addRunArtifact(tests);
    // Fixture paths in the suite are relative to this directory.
    run_tests.setCwd(b.path("."));

    const test_step = b.step("test", "Run the core and C ABI test suites");
    test_step.dependOn(&run_tests.step);
}

fn addHapPlugin(
    b: *Build,
    target: Build.ResolvedTarget,
    optimize: std.builtin.OptimizeMode,
) *Build.Step.Compile {
    const module = b.createModule(.{
        .root_source_file = b.path("src/bluecadet_hap.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .link_libcpp = true,
    });

    addCSources(b, module, target);

    const lib = b.addLibrary(.{
        .name = "bluecadet_hap",
        .root_module = module,
        .linkage = .dynamic,
    });

    if (optimize != .Debug) {
        lib.root_module.strip = true;
        lib.link_gc_sections = true;
    }

    if (target.result.os.tag.isDarwin()) {
        // Unity expects the native plugin to keep the historical .bundle name.
        lib.out_filename = b.dupe("bluecadet_hap.bundle");
        lib.out_lib_filename = lib.out_filename;
    }

    return lib;
}

fn artifactFileName(target: std.Target) []const u8 {
    return switch (target.os.tag) {
        .macos => "bluecadet_hap.bundle",
        .windows => "bluecadet_hap.dll",
        .linux => "libbluecadet_hap.so",
        else => "bluecadet_hap",
    };
}

/// Plugins/ subdirectory for `target`: the name of the `supported_targets`
/// entry it matches, so a host build installs beside the cross builds
/// instead of into a second directory spelled its own way. An unsupported
/// host (nothing Unity ships) falls back to the tag names.
fn targetDirectoryName(b: *Build, target: std.Target) []const u8 {
    for (supported_targets) |spec| {
        if (spec.query.os_tag == target.os.tag and spec.query.cpu_arch == target.cpu.arch) {
            return spec.name;
        }
    }
    return b.fmt("{t}-{t}", .{ target.os.tag, target.cpu.arch });
}
