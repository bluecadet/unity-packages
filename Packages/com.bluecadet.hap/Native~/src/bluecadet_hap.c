/*
 * bluecadet_hap.c — Glue layer: demux + decode behind a simple public API
 */

#include "bluecadet_hap.h"
#include "hap_demux.h"
#include "hap_decode.h"
#include <limits.h>
#include <stdlib.h>
#include <string.h>

struct HapHandle {
    HapDemux    *demux;
    HapDecoder  *decoder;
    uint8_t     *sample_buf;
    int          sample_buf_size;
    int          last_sample_size; /* bytes written by last hap_read_sample call */
    int          texture_format;   /* cached after first decode */
    int          frame_buffer_size;
};

/* Compute DXT buffer size from dimensions and format.
 * Returns -1 if dimensions are invalid or the result would overflow int. */
static int compute_frame_buffer_size(int width, int height, int hap_tex_format)
{
    if (width <= 0 || height <= 0) return -1;

    int block_size;
    switch (hap_tex_format) {
        case 1: /* DXT1 */
            block_size = 8;
            break;
        case 2: /* DXT5 */
        case 3: /* BC7 */
        case 4: /* YCoCg_DXT5 */
            block_size = 16;
            break;
        default:
            block_size = 8;
            break;
    }
    int blocks_x = (width  + 3) / 4;
    int blocks_y = (height + 3) / 4;

    /* Guard against integer overflow: use int64 arithmetic then range-check. */
    int64_t size = (int64_t)blocks_x * blocks_y * block_size;
    if (size <= 0 || size > INT_MAX) return -1;
    return (int)size;
}

HapHandle *hap_open(const char *path, HapError *err)
{
    if (!path) {
        if (err) *err = HAP_ERROR_ARGS;
        return NULL;
    }

    HapDemux *demux = hap_demux_open(path);
    if (!demux) {
        if (err) *err = HAP_ERROR_FILE;
        return NULL;
    }

    HapDecoder *decoder = hap_decoder_create(0);
    if (!decoder) {
        hap_demux_close(demux);
        if (err) *err = HAP_ERROR_DECODE;
        return NULL;
    }

    int max_sample = hap_demux_get_max_sample_size(demux);
    /* Reject files where all frames appear empty or the sample table is corrupt. */
    if (max_sample <= 0) {
        hap_decoder_destroy(decoder);
        hap_demux_close(demux);
        if (err) *err = HAP_ERROR_FILE;
        return NULL;
    }
    uint8_t *sample_buf = (uint8_t *)malloc((size_t)max_sample);
    if (!sample_buf) {
        hap_decoder_destroy(decoder);
        hap_demux_close(demux);
        if (err) *err = HAP_ERROR_DECODE;
        return NULL;
    }

    HapHandle *h = (HapHandle *)calloc(1, sizeof(HapHandle));
    if (!h) {
        free(sample_buf);
        hap_decoder_destroy(decoder);
        hap_demux_close(demux);
        if (err) *err = HAP_ERROR_DECODE;
        return NULL;
    }

    h->demux = demux;
    h->decoder = decoder;
    h->sample_buf = sample_buf;
    h->sample_buf_size = max_sample;

    /* Probe texture format by decoding first frame into a temp buffer */
    int width = hap_demux_get_width(demux);
    int height = hap_demux_get_height(demux);

    /* Start with DXT1 size as conservative estimate, then adjust.
     * compute_frame_buffer_size returns -1 on bad dimensions; propagating that
     * would silently corrupt the size check in hap_decompress_frame. */
    h->texture_format = 1; /* DXT1 default */
    h->frame_buffer_size = compute_frame_buffer_size(width, height, 1);
    if (h->frame_buffer_size <= 0) {
        free(sample_buf);
        hap_decoder_destroy(decoder);
        hap_demux_close(demux);
        free(h);
        if (err) *err = HAP_ERROR_FORMAT;
        return NULL;
    }

    if (hap_demux_get_frame_count(demux) > 0) {
        int sample_size = hap_demux_read_sample(demux, 0, sample_buf, max_sample);
        if (sample_size > 0) {
            /* Allocate max possible (BC7/DXT5 size) for probe */
            int max_buf = compute_frame_buffer_size(width, height, 2);
            if (max_buf > 0) {
                uint8_t *probe = (uint8_t *)malloc((size_t)max_buf);
                if (probe) {
                    int fmt = 0;
                    if (hap_decoder_decode(decoder, sample_buf, sample_size,
                                           probe, max_buf, &fmt) == 0) {
                        int fmt_size = compute_frame_buffer_size(width, height, fmt);
                        if (fmt_size > 0) {
                            h->texture_format = fmt;
                            h->frame_buffer_size = fmt_size;
                        }
                    }
                    free(probe);
                }
            }
        }
    }

    if (err) *err = HAP_ERROR_NONE;
    return h;
}

void hap_close(HapHandle *h)
{
    if (!h) return;
    free(h->sample_buf);
    hap_decoder_destroy(h->decoder);
    hap_demux_close(h->demux);
    free(h);
}

int hap_get_width(HapHandle *h)
{
    return h ? hap_demux_get_width(h->demux) : 0;
}

int hap_get_height(HapHandle *h)
{
    return h ? hap_demux_get_height(h->demux) : 0;
}

int hap_get_frame_count(HapHandle *h)
{
    return h ? hap_demux_get_frame_count(h->demux) : 0;
}

float hap_get_frame_rate(HapHandle *h)
{
    return h ? hap_demux_get_frame_rate(h->demux) : 0.0f;
}

int hap_get_texture_format(HapHandle *h)
{
    return h ? h->texture_format : 0;
}

int hap_get_frame_buffer_size(HapHandle *h)
{
    return h ? h->frame_buffer_size : 0;
}

int hap_read_sample(HapHandle *h, int frame_index)
{
    /* Validate all fields that hap_demux_read_sample will use.  Calling this
     * function on a partially-constructed or freed handle has caused fatal
     * crashes inside the demuxer; fail early with a recoverable error code. */
    if (!h || !h->demux || !h->sample_buf || h->sample_buf_size <= 0) return -1;
    h->last_sample_size = 0;  /* clear first so a failed read can never leave stale data */
    int n = hap_demux_read_sample(h->demux, frame_index,
                                   h->sample_buf, h->sample_buf_size);
    if (n > 0) h->last_sample_size = n;
    return n;
}

int hap_decompress_frame(HapHandle *h, uint8_t *buf, int size)
{
    /* Reject non-positive sizes: a -1 from compute_frame_buffer_size stored in
     * frame_buffer_size would otherwise pass the `size < frame_buffer_size` check
     * when size == -1, allowing HapDecode to receive SIZE_MAX as output_size. */
    if (!h || !buf || size <= 0 || h->frame_buffer_size <= 0 ||
        size < h->frame_buffer_size ||
        h->last_sample_size <= 0 || h->last_sample_size > h->sample_buf_size)
        return HAP_ERROR_ARGS;

    int fmt = 0;
    int ret = hap_decoder_decode(h->decoder, h->sample_buf, h->last_sample_size,
                                  buf, size, &fmt);
    return ret != 0 ? HAP_ERROR_DECODE : HAP_ERROR_NONE;
}

int hap_decode_frame(HapHandle *h, int frame_index, uint8_t *buf, int size)
{
    int n = hap_read_sample(h, frame_index);
    if (n <= 0) return HAP_ERROR_FILE;
    return hap_decompress_frame(h, buf, size);
}

void hap_set_thread_count(HapHandle *h, int count)
{
    if (!h) return;
    hap_decoder_set_thread_count(h->decoder, count);
}
