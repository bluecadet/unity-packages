/*
 * hap_demux.c — MOV/MP4 demuxer using minimp4
 */

#include "hap_demux.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MINIMP4_ALLOW_64BIT 1
#define MINIMP4_IMPLEMENTATION
#include "minimp4.h"

struct HapDemux {
    FILE          *file;
    int64_t        file_size;
    MP4D_demux_t   mp4;
    int            track_index;
    int            width;
    int            height;
    int            frame_count;
    float          frame_rate;
    int            max_sample_size;
};

static int mp4_read_callback(int64_t offset, void *buffer, size_t size, void *token)
{
    FILE *f = (FILE *)token;
    if (fseek(f, (long)offset, SEEK_SET) != 0)
        return -1;
    size_t rd = fread(buffer, 1, size, f);
    return (rd == size) ? 0 : -1;
}

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8)  |  (uint32_t)p[3];
}

static uint16_t read_be16(const uint8_t *p)
{
    return (uint16_t)(((uint16_t)p[0] << 8) | (uint16_t)p[1]);
}

/*
 * Search the moov atom for the stsd box of a given track and extract
 * the VisualSampleEntry width/height. Works for any FourCC (Hap1, HapY, etc).
 *
 * VisualSampleEntry layout (ISO 14496-12):
 *   uint8[6] reserved
 *   uint16   data_reference_index
 *   uint16   pre_defined
 *   uint16   reserved
 *   uint32[3] pre_defined
 *   uint16   width
 *   uint16   height
 *   ... (the rest we don't need)
 */
static int parse_stsd_dimensions(FILE *f, int64_t file_size,
                                  MP4D_demux_t *mp4, int track_idx,
                                  int *out_w, int *out_h)
{
    /* The track's stsd_data may be populated by minimp4. If not, we search manually.
     * minimp4 stores per-sample offsets; we can get the first sample and look backwards,
     * but it's simpler to just search the file for the stsd of this track.
     *
     * Alternative approach: read the first frame data and use HapGetFrameTextureFormat,
     * but that doesn't give us dimensions.
     *
     * Simplest reliable approach: use minimp4's internal stsd pointer if available,
     * otherwise scan the moov for stsd. Since minimp4 doesn't expose stsd directly,
     * we'll scan. */

    /* Find moov atom */
    int64_t pos = 0;
    int64_t moov_offset = -1;
    int64_t moov_size = 0;
    uint8_t hdr[16];

    while (pos < file_size) {
        if (fseek(f, (long)pos, SEEK_SET) != 0) break;
        if (fread(hdr, 1, 8, f) != 8) break;

        uint64_t sz = read_be32(hdr);
        uint32_t typ = read_be32(hdr + 4);

        if (sz == 1) {
            if (fread(hdr + 8, 1, 8, f) != 8) break;
            sz = ((uint64_t)read_be32(hdr + 8) << 32) | read_be32(hdr + 12);
        }
        if (sz == 0) sz = (uint64_t)(file_size - pos);

        if (typ == 0x6d6f6f76) { /* 'moov' */
            moov_offset = pos + 8;
            moov_size = (int64_t)sz - 8;
            break;
        }
        pos += (int64_t)sz;
    }

    if (moov_offset < 0) return 0;

    /* Read moov into memory */
    if (moov_size > 64 * 1024 * 1024) return 0; /* safety limit */
    uint8_t *moov = (uint8_t *)malloc((size_t)moov_size);
    if (!moov) return 0;

    if (fseek(f, (long)moov_offset, SEEK_SET) != 0 ||
        fread(moov, 1, (size_t)moov_size, f) != (size_t)moov_size) {
        free(moov);
        return 0;
    }

    /* Find the Nth trak, then its stsd */
    int trak_count = 0;
    int found = 0;

    /* Simple recursive atom scanner */
    /* We need: moov > trak[track_idx] > mdia > minf > stbl > stsd */
    /* For simplicity, find all 'stsd' atoms and pick the one in our track */

    /* Count which trak we're in by finding trak boundaries */
    /* Walk top-level children of moov to find trak #track_idx */
    int64_t trak_start = -1, trak_end = -1;
    int64_t off = 0;
    int trak_n = 0;
    while (off < moov_size) {
        if (off + 8 > moov_size) break;
        uint32_t bsz = read_be32(moov + off);
        uint32_t btyp = read_be32(moov + off + 4);
        if (bsz < 8 || off + bsz > moov_size) break;
        if (btyp == 0x7472616b) { /* 'trak' */
            if (trak_n == track_idx) {
                trak_start = off + 8;
                trak_end = off + bsz;
                break;
            }
            trak_n++;
        }
        off += bsz;
    }

    if (trak_start < 0) { free(moov); return 0; }

    /* Search recursively within this trak for 'stsd' */
    /* Simple: just scan for the 4-byte pattern 'stsd' and validate */
    for (int64_t s = trak_start; s < trak_end - 8; s++) {
        if (read_be32(moov + s + 4) == 0x73747364) { /* 'stsd' */
            uint32_t stsd_sz = read_be32(moov + s);
            if (stsd_sz < 24 || s + stsd_sz > trak_end) continue;

            /* stsd: version(1) + flags(3) + entry_count(4) = 8 bytes after box header */
            int64_t entry_off = s + 8 + 8; /* box header + fullbox fields + entry_count */
            /* Actually: s+8 = after box header, +4 = version/flags, +4 = entry_count */
            /* Entry starts at s + 16 */
            entry_off = s + 16;

            if (entry_off + 8 > trak_end) continue;

            uint32_t entry_sz = read_be32(moov + entry_off);
            /* uint32_t entry_fourcc = read_be32(moov + entry_off + 4); */

            if (entry_sz < 40 || entry_off + entry_sz > trak_end) continue;

            /* VisualSampleEntry: offset +8(header) +6(reserved) +2(dri) +2+2+12 = +32 from entry start */
            int64_t dim_off = entry_off + 8 + 6 + 2 + 2 + 2 + 12;
            if (dim_off + 4 > trak_end) continue;

            *out_w = read_be16(moov + dim_off);
            *out_h = read_be16(moov + dim_off + 2);

            if (*out_w > 0 && *out_h > 0) {
                found = 1;
                break;
            }
        }
    }

    free(moov);
    return found;
}

HapDemux *hap_demux_open(const char *path)
{
    if (!path) return NULL;

    FILE *f = fopen(path, "rb");
    if (!f) return NULL;

    fseek(f, 0, SEEK_END);
    int64_t fsize = (int64_t)ftell(f);
    fseek(f, 0, SEEK_SET);

    if (fsize <= 0) {
        fclose(f);
        return NULL;
    }

    HapDemux *d = (HapDemux *)calloc(1, sizeof(HapDemux));
    if (!d) {
        fclose(f);
        return NULL;
    }

    d->file = f;
    d->file_size = fsize;

    if (MP4D_open(&d->mp4, mp4_read_callback, f, fsize) == 0) {
        fclose(f);
        free(d);
        return NULL;
    }

    /* Find the first track with samples that has video dimensions.
     * minimp4 may not identify HAP tracks as video, so we parse stsd ourselves. */
    d->track_index = -1;
    for (int i = 0; i < (int)d->mp4.track_count; i++) {
        MP4D_track_t *track = &d->mp4.track[i];

        /* First check if minimp4 already got the dimensions */
        if (track->handler_type == MP4D_HANDLER_TYPE_VIDE &&
            track->SampleDescription.video.width > 0) {
            d->track_index = i;
            d->width = track->SampleDescription.video.width;
            d->height = track->SampleDescription.video.height;
        } else if (track->sample_count > 0) {
            /* Try parsing stsd ourselves for HAP/unknown codecs */
            int w = 0, h = 0;
            if (parse_stsd_dimensions(f, fsize, &d->mp4, i, &w, &h) && w > 0 && h > 0) {
                d->track_index = i;
                d->width = w;
                d->height = h;
            }
        }

        if (d->track_index >= 0) {
            d->frame_count = (int)track->sample_count;

            if (track->duration_hi == 0 && track->duration_lo > 0) {
                double duration_sec = (double)track->duration_lo / (double)track->timescale;
                d->frame_rate = (float)((double)track->sample_count / duration_sec);
            } else {
                d->frame_rate = 30.0f;
            }
            break;
        }
    }

    if (d->track_index < 0) {
        MP4D_close(&d->mp4);
        fclose(f);
        free(d);
        return NULL;
    }

    /* Calculate max sample size */
    d->max_sample_size = 0;
    for (int i = 0; i < d->frame_count; i++) {
        unsigned frame_bytes, timestamp, duration;
        MP4D_file_offset_t ofs = MP4D_frame_offset(&d->mp4, d->track_index, i,
                                                     &frame_bytes, &timestamp, &duration);
        (void)ofs;
        if ((int)frame_bytes > d->max_sample_size)
            d->max_sample_size = (int)frame_bytes;
    }

    return d;
}

void hap_demux_close(HapDemux *d)
{
    if (!d) return;
    MP4D_close(&d->mp4);
    if (d->file) fclose(d->file);
    free(d);
}

int hap_demux_get_width(HapDemux *d)       { return d ? d->width : 0; }
int hap_demux_get_height(HapDemux *d)      { return d ? d->height : 0; }
int hap_demux_get_frame_count(HapDemux *d) { return d ? d->frame_count : 0; }
float hap_demux_get_frame_rate(HapDemux *d){ return d ? d->frame_rate : 0.0f; }
int hap_demux_get_max_sample_size(HapDemux *d) { return d ? d->max_sample_size : 0; }

int hap_demux_read_sample(HapDemux *d, int frame_index, uint8_t *buf, int buf_size)
{
    if (!d || frame_index < 0 || frame_index >= d->frame_count)
        return -1;

    unsigned frame_bytes, timestamp, duration;
    MP4D_file_offset_t ofs = MP4D_frame_offset(&d->mp4, d->track_index, frame_index,
                                                 &frame_bytes, &timestamp, &duration);

    if (ofs == 0 || frame_bytes == 0)
        return -1;

    if (!buf)
        return (int)frame_bytes;

    if (buf_size < (int)frame_bytes)
        return -1;

    if (fseek(d->file, (long)ofs, SEEK_SET) != 0)
        return -1;

    size_t rd = fread(buf, 1, (size_t)frame_bytes, d->file);
    if (rd != (size_t)frame_bytes)
        return -1;

    return (int)frame_bytes;
}
