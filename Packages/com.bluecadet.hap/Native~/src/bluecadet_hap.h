/*
 * bluecadet_hap.h -- public C API of the bluecadet_hap native plugin.
 *
 * Hand-maintained mirror of the exports in src/bluecadet_hap.zig (the
 * implementation); the C# bindings in Scripts/HapNative.cs are written
 * against this file. Keep all three in sync.
 *
 * Contract
 * --------
 *  * Fallible calls return an int32_t HapError code; HAP_OK (0) is success.
 *    Getters return 0 for a NULL handle instead of an error code.
 *  * A handle owns everything reachable from it (the file mapping, the
 *    sample table, the decode scratch buffer). hap_close() frees all of it
 *    and accepts NULL.
 *  * Calls on one handle must be serialized by the caller (the intended
 *    use is a single decode thread per open file). Different handles may
 *    be used concurrently from different threads with no coordination.
 *  * hap_set_thread_count() is process-global and safe to call from any
 *    thread at any time.
 *
 * Texture model
 * -------------
 * A frame carries one texture for Hap / Hap Alpha / Hap Q / Hap R, and two
 * for Hap Q Alpha (texture 0 = YCoCg color, texture 1 = RGTC1 alpha). All
 * texture queries and hap_decode_texture() are indexed by texture, so each
 * one can be uploaded to its own GPU resource. Both textures of a frame
 * decode from the same demuxed sample, which the handle caches between the
 * two calls.
 */
#ifndef BLUECADET_HAP_H
#define BLUECADET_HAP_H

#include <stdint.h>

#ifdef _WIN32
  #define HAP_EXPORT __declspec(dllexport)
#else
  #define HAP_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct HapHandle HapHandle;

/* Result codes returned by every fallible entry point. */
typedef enum HapError {
    HAP_OK                          = 0,
    /* A NULL pointer, an out-of-range index, or a nonsensical count. */
    HAP_ERROR_INVALID_ARGUMENT      = 1,
    /* The path could not be opened (missing file, no permission). */
    HAP_ERROR_FILE_NOT_FOUND        = 2,
    /* The file exists but could not be stat'd or memory-mapped. */
    HAP_ERROR_FILE_READ             = 3,
    /* Not a parseable MP4/MOV container. */
    HAP_ERROR_NOT_A_MOV             = 4,
    /* A container, but with no Hap video track in it. */
    HAP_ERROR_NO_HAP_TRACK          = 5,
    /* A Hap track whose variant cannot be decoded (HapA, Hap HDR). */
    HAP_ERROR_UNSUPPORTED_VARIANT   = 6,
    /* The Hap track's sample table is empty or inconsistent with the file. */
    HAP_ERROR_CORRUPT_TRACK         = 7,
    /* frame_index is outside [0, hap_get_frame_count). */
    HAP_ERROR_FRAME_OUT_OF_RANGE    = 8,
    /* The frame's bytes are not a valid/supported Hap frame. */
    HAP_ERROR_INVALID_FRAME         = 9,
    /* The supplied buffer is smaller than the decoded texture. */
    HAP_ERROR_BUFFER_TOO_SMALL      = 10,
    /* An allocation failed. */
    HAP_ERROR_OUT_OF_MEMORY         = 11
} HapError;

/* Compressed texture layout of one decoded texture, as returned by
 * hap_get_texture_format(). 0 is returned for an invalid handle/index. */
typedef enum HapTextureFormatCode {
    HAP_FORMAT_DXT1        = 1, /* BC1  -- Hap */
    HAP_FORMAT_DXT5        = 2, /* BC3  -- Hap Alpha */
    HAP_FORMAT_BC7         = 3, /* BC7  -- Hap R */
    HAP_FORMAT_YCOCG_DXT5  = 4, /* BC3 carrying scaled YCoCg -- Hap Q, Hap Q Alpha texture 0 */
    HAP_FORMAT_RGTC1       = 5  /* BC4  -- Hap Q Alpha texture 1 (alpha) */
} HapTextureFormatCode;

/* Open a Hap MOV file at a UTF-8 path. On success writes the new handle to
 * *out_handle and returns HAP_OK; on failure writes NULL and returns the
 * reason. Opening also parses frame 0's texture layout, so a file whose
 * first frame is not a valid Hap frame fails here with
 * HAP_ERROR_INVALID_FRAME.
 *
 * Note: on Windows the path goes through the ANSI file API, so non-ASCII
 * paths are a known limitation. */
HAP_EXPORT int32_t hap_open(const char *path, HapHandle **out_handle);

/* Release a handle and everything it owns. NULL is a no-op. */
HAP_EXPORT void    hap_close(HapHandle *h);

/* Track metadata. All return 0 for a NULL handle. */
HAP_EXPORT int32_t hap_get_width(HapHandle *h);
HAP_EXPORT int32_t hap_get_height(HapHandle *h);
HAP_EXPORT int32_t hap_get_frame_count(HapHandle *h);
HAP_EXPORT float   hap_get_frame_rate(HapHandle *h);

/* Number of textures each frame carries: 1, or 2 for Hap Q Alpha. */
HAP_EXPORT int32_t hap_get_texture_count(HapHandle *h);

/* HapTextureFormatCode of texture tex_index, or 0 if the handle is NULL or
 * the index is out of range. Read from frame 0 at open time; the Hap
 * bitstream carries the format per frame, but every frame of a file shares
 * it in practice. */
HAP_EXPORT int32_t hap_get_texture_format(HapHandle *h, int32_t tex_index);

/* Decoded byte size of texture tex_index -- the buffer size
 * hap_decode_texture() needs -- or 0 if the handle is NULL or the index is
 * out of range. Computed as ceil(width/4) * ceil(height/4) * block bytes
 * (8 for DXT1/RGTC1, 16 for DXT5/YCoCg-DXT5/BC7). */
HAP_EXPORT int32_t hap_get_texture_buffer_size(HapHandle *h, int32_t tex_index);

/* Decode texture tex_index of frame frame_index into buf.
 *
 * buf_size must be at least hap_get_texture_buffer_size(h, tex_index);
 * a larger buffer is fine (only the decoded bytes are written) and a
 * smaller one returns HAP_ERROR_BUFFER_TOO_SMALL without touching buf.
 * For Hap Q Alpha, decoding texture 0 then texture 1 of the same frame
 * reuses the sample read for the first call. */
HAP_EXPORT int32_t hap_decode_texture(HapHandle *h, int32_t frame_index,
                                      int32_t tex_index, uint8_t *buf,
                                      int32_t buf_size);

/* Set how many threads decode a chunked frame's chunks in parallel,
 * process-wide (not per handle). thread_count includes the calling decode
 * thread, which always decodes a share itself, so 1 means "no helper
 * threads". Values above the shared pool's size are clamped to it. Takes
 * effect on the next chunked frame decoded; returns
 * HAP_ERROR_INVALID_ARGUMENT for thread_count < 1, else HAP_OK. */
HAP_EXPORT int32_t hap_set_thread_count(int32_t thread_count);

#ifdef __cplusplus
}
#endif

#endif /* BLUECADET_HAP_H */
