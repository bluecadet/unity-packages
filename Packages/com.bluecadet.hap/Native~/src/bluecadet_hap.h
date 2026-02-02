/*
 * bluecadet_hap.h — Public C API for HAP video decode plugin
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
typedef int HapError;

#define HAP_ERROR_NONE       0
#define HAP_ERROR_FILE       1
#define HAP_ERROR_FORMAT     2
#define HAP_ERROR_DECODE     3
#define HAP_ERROR_ARGS       4

HAP_EXPORT HapHandle* hap_open(const char *path, HapError *err);
HAP_EXPORT void       hap_close(HapHandle *h);
HAP_EXPORT int        hap_get_width(HapHandle *h);
HAP_EXPORT int        hap_get_height(HapHandle *h);
HAP_EXPORT int        hap_get_frame_count(HapHandle *h);
HAP_EXPORT float      hap_get_frame_rate(HapHandle *h);
HAP_EXPORT int        hap_get_texture_format(HapHandle *h);
HAP_EXPORT int        hap_get_frame_buffer_size(HapHandle *h);
HAP_EXPORT int        hap_decode_frame(HapHandle *h, int frame_index,
                                        uint8_t *buf, int size);
HAP_EXPORT void       hap_set_thread_count(HapHandle *h, int count);

#ifdef __cplusplus
}
#endif

#endif /* BLUECADET_HAP_H */
