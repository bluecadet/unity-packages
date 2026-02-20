/*
 * hap_decode.h — HAP frame decoder with thread pool
 */
#ifndef HAP_DECODE_H
#define HAP_DECODE_H

#include <stdint.h>

typedef struct HapDecoder HapDecoder;

/* Internal texture format constants. These are translated to Unity TextureFormat in HapTextureUploader.
 * They do NOT match Unity's TextureFormat enum values. */
#define HAP_TEX_FORMAT_DXT1        1
#define HAP_TEX_FORMAT_DXT5        2
#define HAP_TEX_FORMAT_BC7         3
#define HAP_TEX_FORMAT_YCOCG_DXT5  4  /* HAP Q — DXT5 with YCoCg color space, needs shader decode */

/*
 * Create a decoder. thread_count=0 means auto-detect.
 */
HapDecoder *hap_decoder_create(int thread_count);
void hap_decoder_destroy(HapDecoder *dec);
void hap_decoder_set_thread_count(HapDecoder *dec, int count);

/*
 * Decode a single HAP frame.
 * input/input_size: compressed HAP frame data (from demuxer).
 * output/output_size: pre-allocated buffer for decoded DXT data.
 * Returns 0 on success, non-zero on error.
 * out_texture_format is set to one of HAP_TEX_FORMAT_*.
 */
int hap_decoder_decode(HapDecoder *dec,
                       const uint8_t *input, int input_size,
                       uint8_t *output, int output_size,
                       int *out_texture_format);

#endif /* HAP_DECODE_H */
