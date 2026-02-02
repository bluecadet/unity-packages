/*
 * hap_demux.h — MOV/MP4 demuxer wrapper around minimp4
 */
#ifndef HAP_DEMUX_H
#define HAP_DEMUX_H

#include <stdint.h>
#include <stddef.h>

typedef struct HapDemux HapDemux;

/* Open a MOV/MP4 file. Returns NULL on failure. */
HapDemux *hap_demux_open(const char *path);

/* Close and free all resources. */
void hap_demux_close(HapDemux *d);

/* Video track metadata. */
int   hap_demux_get_width(HapDemux *d);
int   hap_demux_get_height(HapDemux *d);
int   hap_demux_get_frame_count(HapDemux *d);
float hap_demux_get_frame_rate(HapDemux *d);

/*
 * Read a compressed sample by index.
 * Caller provides buffer and size. Returns bytes written, or -1 on error.
 * If buf is NULL, returns required size.
 */
int hap_demux_read_sample(HapDemux *d, int frame_index, uint8_t *buf, int buf_size);

/*
 * Get the maximum sample size across all frames (for buffer pre-allocation).
 */
int hap_demux_get_max_sample_size(HapDemux *d);

#endif /* HAP_DEMUX_H */
