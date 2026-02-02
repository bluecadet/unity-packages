/*
 * bluecadet_hap.c — Glue layer: demux + decode behind a simple public API
 */

#include "bluecadet_hap.h"
#include "hap_demux.h"
#include "hap_decode.h"
#include <stdlib.h>
#include <string.h>

struct HapHandle {
    HapDemux    *demux;
    HapDecoder  *decoder;
    uint8_t     *sample_buf;
    int          sample_buf_size;
    int          texture_format;  /* cached after first decode */
    int          frame_buffer_size;
};

/* Compute DXT buffer size from dimensions and format */
static int compute_frame_buffer_size(int width, int height, int hap_tex_format)
{
    int block_size;
    switch (hap_tex_format) {
        case 1: /* DXT1 */
            block_size = 8;
            break;
        case 2: /* DXT5 */
        case 3: /* BC7 */
            block_size = 16;
            break;
        default:
            block_size = 8;
            break;
    }
    int blocks_x = (width + 3) / 4;
    int blocks_y = (height + 3) / 4;
    return blocks_x * blocks_y * block_size;
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

    /* Start with DXT1 size as conservative estimate, then adjust */
    h->texture_format = 1; /* DXT1 default */
    h->frame_buffer_size = compute_frame_buffer_size(width, height, 1);

    if (hap_demux_get_frame_count(demux) > 0) {
        int sample_size = hap_demux_read_sample(demux, 0, sample_buf, max_sample);
        if (sample_size > 0) {
            /* Allocate max possible (BC7/DXT5 size) for probe */
            int max_buf = compute_frame_buffer_size(width, height, 2);
            uint8_t *probe = (uint8_t *)malloc((size_t)max_buf);
            if (probe) {
                int fmt = 0;
                if (hap_decoder_decode(decoder, sample_buf, sample_size,
                                       probe, max_buf, &fmt) == 0) {
                    h->texture_format = fmt;
                    h->frame_buffer_size = compute_frame_buffer_size(width, height, fmt);
                }
                free(probe);
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

int hap_decode_frame(HapHandle *h, int frame_index, uint8_t *buf, int size)
{
    if (!h || !buf || size < h->frame_buffer_size)
        return HAP_ERROR_ARGS;

    int sample_size = hap_demux_read_sample(h->demux, frame_index,
                                             h->sample_buf, h->sample_buf_size);
    if (sample_size <= 0)
        return HAP_ERROR_FILE;

    int fmt = 0;
    int ret = hap_decoder_decode(h->decoder, h->sample_buf, sample_size,
                                  buf, size, &fmt);
    if (ret != 0)
        return HAP_ERROR_DECODE;

    return HAP_ERROR_NONE;
}

void hap_set_thread_count(HapHandle *h, int count)
{
    if (!h) return;
    hap_decoder_set_thread_count(h->decoder, count);
}
