/*
 * hap_demux.c — MOV/MP4 demuxer using minimp4, with memory-mapped I/O
 *
 * Using memory-mapped files instead of fseek/fread eliminates per-frame seek
 * overhead, which is the dominant cost during scrubbing. The OS page cache
 * naturally retains recently-visited frames, and the kernel handles sequential
 * read-ahead during normal linear playback automatically.
 *
 * All frame offsets and sizes are pre-cached at open time so that
 * hap_demux_read_sample is O(1) regardless of container complexity.
 */

#include "hap_demux.h"
#include <stdlib.h>
#include <string.h>

/* ── Platform-specific memory-mapped file ───────────────────────────────── */

#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>

typedef struct {
    HANDLE file_handle;
    HANDLE mapping_handle;
} PlatformFile;

static int mapped_file_open(PlatformFile *pf, const char *path,
                             const uint8_t **out_data, int64_t *out_size)
{
    pf->file_handle    = INVALID_HANDLE_VALUE;
    pf->mapping_handle = NULL;

    /* FILE_FLAG_RANDOM_ACCESS tells Windows not to bother with sequential
     * read-ahead prefetch, which is counter-productive during scrubbing. */
    pf->file_handle = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ,
                                   NULL, OPEN_EXISTING,
                                   FILE_ATTRIBUTE_NORMAL | FILE_FLAG_RANDOM_ACCESS,
                                   NULL);
    if (pf->file_handle == INVALID_HANDLE_VALUE) return 0;

    LARGE_INTEGER li;
    if (!GetFileSizeEx(pf->file_handle, &li) || li.QuadPart == 0)
        goto fail;

    pf->mapping_handle = CreateFileMapping(pf->file_handle, NULL,
                                            PAGE_READONLY, 0, 0, NULL);
    if (!pf->mapping_handle) goto fail;

    *out_data = (const uint8_t *)MapViewOfFile(pf->mapping_handle,
                                                FILE_MAP_READ, 0, 0, 0);
    if (!*out_data) goto fail;

    *out_size = (int64_t)li.QuadPart;
    return 1;

fail:
    if (pf->mapping_handle) { CloseHandle(pf->mapping_handle); pf->mapping_handle = NULL; }
    if (pf->file_handle != INVALID_HANDLE_VALUE) { CloseHandle(pf->file_handle); pf->file_handle = INVALID_HANDLE_VALUE; }
    return 0;
}

static void mapped_file_close(PlatformFile *pf, const uint8_t *data, int64_t size)
{
    (void)size;
    if (data)                  UnmapViewOfFile(data);
    if (pf->mapping_handle)    CloseHandle(pf->mapping_handle);
    if (pf->file_handle != INVALID_HANDLE_VALUE) CloseHandle(pf->file_handle);
}

#else /* POSIX ──────────────────────────────────────────────────────────── */

#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>

typedef struct { int fd; } PlatformFile;

static int mapped_file_open(PlatformFile *pf, const char *path,
                             const uint8_t **out_data, int64_t *out_size)
{
    pf->fd = open(path, O_RDONLY);
    if (pf->fd < 0) return 0;

    struct stat st;
    if (fstat(pf->fd, &st) != 0 || st.st_size == 0) {
        close(pf->fd); pf->fd = -1;
        return 0;
    }

    void *p = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, pf->fd, 0);
    if (p == MAP_FAILED) {
        close(pf->fd); pf->fd = -1;
        return 0;
    }

    /* The fd can be closed once mapped; the mapping remains valid. */
    close(pf->fd);
    pf->fd = -1;

    *out_data = (const uint8_t *)p;
    *out_size = (int64_t)st.st_size;
    return 1;
}

static void mapped_file_close(PlatformFile *pf, const uint8_t *data, int64_t size)
{
    (void)pf;
    if (data && size > 0) munmap((void *)data, (size_t)size);
}

#endif /* _WIN32 / POSIX */

/* ── minimp4 ─────────────────────────────────────────────────────────────── */

#define MINIMP4_ALLOW_64BIT 1
#define MP4D_64BIT_SUPPORTED 1
#define MINIMP4_IMPLEMENTATION
#include "minimp4.h"

/* ── HapDemux struct ─────────────────────────────────────────────────────── */

struct HapDemux {
    /* Memory-mapped file */
    PlatformFile        pf;
    const uint8_t      *mapped_data;
    int64_t             mapped_size;

    /* minimp4 demux state */
    MP4D_demux_t        mp4;
    int                 track_index;

    /* Video metadata */
    int                 width;
    int                 height;
    int                 frame_count;
    float               frame_rate;
    int                 max_sample_size;

    /* Pre-cached per-frame layout for O(1) random access during scrubbing */
    MP4D_file_offset_t *frame_offsets;
    unsigned            *frame_sizes;
};

/* ── minimp4 read callback ───────────────────────────────────────────────── */

static int mp4_read_callback(int64_t offset, void *buffer, size_t size, void *token)
{
    HapDemux *d = (HapDemux *)token;
    if (offset < 0 || size == 0) return -1;
    if ((uint64_t)offset + size > (uint64_t)d->mapped_size) return -1;
    memcpy(buffer, d->mapped_data + (size_t)offset, size);
    return 0;
}

/* ── Byte helpers ────────────────────────────────────────────────────────── */

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] <<  8) |  (uint32_t)p[3];
}

static uint16_t read_be16(const uint8_t *p)
{
    return (uint16_t)(((uint16_t)p[0] << 8) | (uint16_t)p[1]);
}

/* ── stsd dimension parser ───────────────────────────────────────────────── */

/*
 * Walk moov > trak[track_idx] > ... > stsd and extract the VisualSampleEntry
 * width/height. Uses direct mapped-memory access — no malloc, no fseek.
 *
 * VisualSampleEntry layout (ISO 14496-12):
 *   uint8[6]  reserved
 *   uint16    data_reference_index
 *   uint16    pre_defined
 *   uint16    reserved
 *   uint32[3] pre_defined
 *   uint16    width
 *   uint16    height
 *   ... (ignored)
 */
static int parse_stsd_dimensions(const uint8_t *data, int64_t file_size,
                                  int track_idx, int *out_w, int *out_h)
{
    /* Locate the 'moov' atom */
    int64_t pos         = 0;
    int64_t moov_offset = -1;
    int64_t moov_size   = 0;

    while (pos + 8 <= file_size) {
        const uint8_t *hdr = data + pos;
        uint64_t sz  = read_be32(hdr);
        uint32_t typ = read_be32(hdr + 4);

        if (sz == 1) {
            if (pos + 16 > file_size) break;
            sz = ((uint64_t)read_be32(hdr + 8) << 32) | read_be32(hdr + 12);
        }
        if (sz == 0) sz = (uint64_t)(file_size - pos);
        if (sz < 8)  break;

        if (typ == 0x6d6f6f76) { /* 'moov' */
            moov_offset = pos + 8;
            /* sz is uint64_t; guard against values that exceed INT64_MAX before
             * narrowing to int64_t.  A moov atom that large is impossible in
             * practice (it would be larger than any real file), but a malformed
             * file could set the field to an extreme value. */
            if (sz - 8 > (uint64_t)INT64_MAX) break;
            moov_size   = (int64_t)(sz - 8);
            break;
        }
        pos += (int64_t)sz;
    }

    if (moov_offset < 0 || moov_offset + moov_size > file_size) return 0;

    /* The moov atom is directly accessible in the mapping — no copy needed. */
    const uint8_t *moov = data + moov_offset;

    /* Find trak #track_idx among moov's top-level children */
    int64_t trak_start = -1, trak_end = -1;
    int64_t off = 0;
    int trak_n = 0;

    while (off + 8 <= moov_size) {
        uint32_t bsz  = read_be32(moov + off);
        uint32_t btyp = read_be32(moov + off + 4);
        if (bsz < 8 || off + bsz > moov_size) break;

        if (btyp == 0x7472616b) { /* 'trak' */
            if (trak_n == track_idx) {
                trak_start = off + 8;
                trak_end   = off + bsz;
                break;
            }
            trak_n++;
        }
        off += bsz;
    }

    if (trak_start < 0) return 0;

    /* Scan the trak body for the 'stsd' box */
    for (int64_t s = trak_start; s + 8 <= trak_end; s++) {
        if (read_be32(moov + s + 4) != 0x73747364) continue; /* 'stsd' */

        uint32_t stsd_sz = read_be32(moov + s);
        if (stsd_sz < 24 || s + stsd_sz > trak_end) continue;

        /* stsd: box header(8) + version/flags(4) + entry_count(4) = 16 bytes before first entry */
        int64_t entry_off = s + 16;
        if (entry_off + 8 > trak_end) continue;

        uint32_t entry_sz = read_be32(moov + entry_off);
        if (entry_sz < 40 || entry_off + entry_sz > trak_end) continue;

        /* VisualSampleEntry offset within entry: header(8) + reserved(6) + dri(2) + pre_def(2) + reserved(2) + pre_def3(12) = 32 */
        int64_t dim_off = entry_off + 32;
        if (dim_off + 4 > trak_end) continue;

        int w = (int)read_be16(moov + dim_off);
        int h = (int)read_be16(moov + dim_off + 2);
        if (w > 0 && h > 0) {
            *out_w = w;
            *out_h = h;
            return 1;
        }
    }

    return 0;
}

/* ── Public API ──────────────────────────────────────────────────────────── */

HapDemux *hap_demux_open(const char *path)
{
    if (!path) return NULL;

    HapDemux *d = (HapDemux *)calloc(1, sizeof(HapDemux));
    if (!d) return NULL;

    /* Map the entire file into virtual memory */
    if (!mapped_file_open(&d->pf, path, &d->mapped_data, &d->mapped_size)) {
        free(d);
        return NULL;
    }

    /* Parse the MP4/MOV container */
    if (MP4D_open(&d->mp4, mp4_read_callback, d, d->mapped_size) == 0) {
        mapped_file_close(&d->pf, d->mapped_data, d->mapped_size);
        free(d);
        return NULL;
    }

    /* Find the video track */
    d->track_index = -1;
    for (int i = 0; i < (int)d->mp4.track_count; i++) {
        MP4D_track_t *track = &d->mp4.track[i];

        if (track->handler_type == MP4D_HANDLER_TYPE_VIDE &&
            track->SampleDescription.video.width > 0) {
            d->track_index = i;
            d->width  = track->SampleDescription.video.width;
            d->height = track->SampleDescription.video.height;
        } else if (track->sample_count > 0) {
            int w = 0, h = 0;
            if (parse_stsd_dimensions(d->mapped_data, d->mapped_size, i, &w, &h) && w > 0 && h > 0) {
                d->track_index = i;
                d->width  = w;
                d->height = h;
            }
        }

        if (d->track_index >= 0) {
            d->frame_count = (int)track->sample_count;
            if (track->duration_hi == 0 && track->duration_lo > 0) {
                double dur = (double)track->duration_lo / (double)track->timescale;
                d->frame_rate = (float)((double)track->sample_count / dur);
            } else {
                d->frame_rate = 30.0f;
            }
            break;
        }
    }

    if (d->track_index < 0) {
        MP4D_close(&d->mp4);
        mapped_file_close(&d->pf, d->mapped_data, d->mapped_size);
        free(d);
        return NULL;
    }

    /* Sanity cap: 16 million frames is ~155 hours at 30 fps — far beyond any
     * real HAP file.  Reject larger values to prevent oversized allocations
     * from a malformed container. */
    if (d->frame_count > (1 << 24)) {
        MP4D_close(&d->mp4);
        mapped_file_close(&d->pf, d->mapped_data, d->mapped_size);
        free(d);
        return NULL;
    }

    /* Pre-cache all frame offsets and sizes.
     * This makes hap_demux_read_sample O(1) and avoids calling MP4D_frame_offset
     * (which walks internal tables) on every single frame during playback. */
    d->frame_offsets = (MP4D_file_offset_t *)malloc((size_t)d->frame_count * sizeof(MP4D_file_offset_t));
    d->frame_sizes   = (unsigned *)           malloc((size_t)d->frame_count * sizeof(unsigned));

    if (!d->frame_offsets || !d->frame_sizes) {
        free(d->frame_offsets);
        free(d->frame_sizes);
        MP4D_close(&d->mp4);
        mapped_file_close(&d->pf, d->mapped_data, d->mapped_size);
        free(d);
        return NULL;
    }

    d->max_sample_size = 0;
    for (int i = 0; i < d->frame_count; i++) {
        unsigned frame_bytes, timestamp, duration;
        MP4D_file_offset_t ofs = MP4D_frame_offset(&d->mp4, d->track_index, i,
                                                     &frame_bytes, &timestamp, &duration);
        d->frame_offsets[i] = ofs;
        d->frame_sizes[i]   = frame_bytes;
        /* Guard against signed overflow: frame_bytes is unsigned, max_sample_size is int.
         * Frames exceeding INT_MAX are rejected in hap_demux_read_sample anyway. */
        if (frame_bytes <= (unsigned)INT_MAX && (int)frame_bytes > d->max_sample_size)
            d->max_sample_size = (int)frame_bytes;
    }

    return d;
}

void hap_demux_close(HapDemux *d)
{
    if (!d) return;
    free(d->frame_offsets);
    free(d->frame_sizes);
    MP4D_close(&d->mp4);
    mapped_file_close(&d->pf, d->mapped_data, d->mapped_size);
    free(d);
}

int   hap_demux_get_width(HapDemux *d)          { return d ? d->width : 0; }
int   hap_demux_get_height(HapDemux *d)         { return d ? d->height : 0; }
int   hap_demux_get_frame_count(HapDemux *d)    { return d ? d->frame_count : 0; }
float hap_demux_get_frame_rate(HapDemux *d)     { return d ? d->frame_rate : 0.0f; }
int   hap_demux_get_max_sample_size(HapDemux *d){ return d ? d->max_sample_size : 0; }

int hap_demux_read_sample(HapDemux *d, int frame_index, uint8_t *buf, int buf_size)
{
    /* Validate struct pointer and internal arrays before any field access.
     * A missing NULL check on frame_offsets/frame_sizes has caused crashes
     * when d points to a partially-initialised or freed struct. */
    if (!d || !d->frame_offsets || !d->frame_sizes) return -1;
    if (frame_index < 0 || frame_index >= d->frame_count) return -1;

    /* O(1) lookup from pre-cached table — no MP4D_frame_offset call needed */
    MP4D_file_offset_t ofs  = d->frame_offsets[frame_index];
    unsigned frame_bytes     = d->frame_sizes[frame_index];

    if (frame_bytes == 0 || ofs == 0) return -1;

    /* Guard against signed overflow: frame_bytes is unsigned; casting a value
     * > INT_MAX to int wraps negative, breaking both the buf_size check and
     * the return value.  Reject oversized frames before the comparison. */
    if (frame_bytes > (unsigned)INT_MAX) return -1;

    if (!buf) return (int)frame_bytes;

    /* Use unsigned comparison so buf_size=0 is caught correctly. */
    if ((unsigned)buf_size < frame_bytes) return -1;

    if ((uint64_t)ofs + frame_bytes > (uint64_t)d->mapped_size) return -1;

    /* Copy from mapped memory — no fseek, no fread, no syscall */
    memcpy(buf, d->mapped_data + (size_t)ofs, frame_bytes);
    return (int)frame_bytes;
}
