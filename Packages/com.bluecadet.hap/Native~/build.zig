const std = @import("std");

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

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const default_lib = addHapPlugin(b, target, optimize);
    const default_dest = b.fmt("{s}/{s}", .{
        targetDirectoryName(b, target.result),
        artifactFileName(target.result),
    });
    const default_install = b.addInstallFile(default_lib.getEmittedBin(), default_dest);
    b.getInstallStep().dependOn(&default_install.step);

    const all_step = b.step("all", "Build every supported Unity native plugin target");
    for (supported_targets) |spec| {
        const resolved = b.resolveTargetQuery(spec.query);
        const lib = addHapPlugin(b, resolved, optimize);
        const dest = b.fmt("{s}/{s}", .{ spec.name, artifactFileName(resolved.result) });
        const install = b.addInstallFile(lib.getEmittedBin(), dest);
        all_step.dependOn(&install.step);
    }
}

fn addHapPlugin(
    b: *std.Build,
    target: std.Build.ResolvedTarget,
    optimize: std.builtin.OptimizeMode,
) *std.Build.Step.Compile {
    const module = b.createModule(.{
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .link_libcpp = true,
    });

    module.addIncludePath(b.path("snappy_config"));
    module.addIncludePath(b.path("src"));
    module.addIncludePath(b.path("vendor/hap"));
    module.addIncludePath(b.path("vendor/minimp4"));
    module.addIncludePath(b.path("vendor/snappy"));

    module.addCMacro("HAVE_CONFIG_H", "1");
    if (target.result.os.tag == .windows) {
        module.addCMacro("_CRT_SECURE_NO_WARNINGS", "1");
        module.linkSystemLibrary("kernel32", .{});
        module.linkSystemLibrary("winmm", .{});
    } else {
        if (target.result.os.tag == .linux) {
            module.addCMacro("_GNU_SOURCE", "1");
        }
        module.linkSystemLibrary("pthread", .{});
    }

    module.addCSourceFiles(.{
        .files = &.{
            "src/bluecadet_hap.c",
            "src/hap_demux.c",
            "src/hap_decode.c",
            "vendor/hap/hap.c",
        },
        .flags = &.{
            "-std=c11",
            "-fvisibility=hidden",
        },
        .language = .c,
    });

    module.addCSourceFiles(.{
        .files = &.{
            "vendor/snappy/snappy-c.cc",
            "vendor/snappy/snappy-sinksource.cc",
            "vendor/snappy/snappy-stubs-internal.cc",
            "vendor/snappy/snappy.cc",
        },
        .flags = &.{
            "-std=c++11",
            "-fno-exceptions",
            "-fno-rtti",
            "-fvisibility=hidden",
            "-Wno-sign-compare",
        },
        .language = .cpp,
    });

    const lib = b.addLibrary(.{
        .name = "bluecadet_hap",
        .root_module = module,
        .linkage = .dynamic,
    });

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

fn targetDirectoryName(b: *std.Build, target: std.Target) []const u8 {
    return b.fmt("{s}-{s}", .{ osName(target.os.tag), archName(target.cpu.arch) });
}

fn osName(tag: std.Target.Os.Tag) []const u8 {
    return switch (tag) {
        .macos => "macos",
        .windows => "windows",
        .linux => "linux",
        else => @tagName(tag),
    };
}

fn archName(arch: std.Target.Cpu.Arch) []const u8 {
    return switch (arch) {
        .aarch64 => "arm64",
        .x86_64 => "x86_64",
        else => @tagName(arch),
    };
}
