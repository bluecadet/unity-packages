/*
 * snappy-c.c — Google-compatible wrapper around andikleen/snappy-c
 *
 * Renames internal symbols to avoid conflicts, then provides the
 * 4-argument API that Vidvox/hap expects.
 */

/* Rename andikleen's public symbols before including snappy.c */
#define snappy_uncompress       snappy_uncompress_raw
#define snappy_uncompressed_length snappy_uncompressed_length_raw
#define snappy_compress         snappy_compress_raw
#define snappy_max_compressed_length snappy_max_compressed_length_raw
#define snappy_init_env         snappy_init_env_raw
#define snappy_init_env_sg      snappy_init_env_sg_raw
#define snappy_free_env         snappy_free_env_raw
#define snappy_compress_iov     snappy_compress_iov_raw
#define snappy_uncompress_iov   snappy_uncompress_iov_raw

/* Prevent andikleen's header from being included directly */
#define _LINUX_SNAPPY_H 1
#include <stdbool.h>
#include <stddef.h>

/* Forward-declare the raw types andikleen needs */
struct snappy_env {
    unsigned short *hash_table;
    void *scratch;
    void *scratch_output;
};
struct iovec;

int    snappy_init_env_raw(struct snappy_env *env);
int    snappy_init_env_sg_raw(struct snappy_env *env, bool sg);
void   snappy_free_env_raw(struct snappy_env *env);
int    snappy_uncompress_raw(const char *compressed, size_t n, char *uncompressed);
int    snappy_uncompress_iov_raw(struct iovec *iov_in, int iov_in_len,
                                  size_t input_len, char *uncompressed);
bool   snappy_uncompressed_length_raw(const char *buf, size_t len, size_t *result);
size_t snappy_max_compressed_length_raw(size_t source_len);
int    snappy_compress_raw(struct snappy_env *env,
                           const char *input, size_t input_length,
                           char *compressed, size_t *compressed_length);
int    snappy_compress_iov_raw(struct snappy_env *env,
                               struct iovec *iov_in, int iov_in_len,
                               size_t input_length,
                               struct iovec *iov_out, int *iov_out_len,
                               size_t *compressed_length);

/* Include the actual implementation with renamed symbols */
#include "snappy.c"

/* Now undefine to provide the Google-compatible API */
#undef snappy_uncompress
#undef snappy_uncompressed_length
#undef snappy_compress
#undef snappy_max_compressed_length
#undef snappy_init_env
#undef snappy_init_env_sg
#undef snappy_free_env
#undef snappy_compress_iov
#undef snappy_uncompress_iov

#include "snappy-c.h"

snappy_status snappy_uncompress(const char *compressed, size_t compressed_length,
                                char *uncompressed, size_t *uncompressed_length)
{
    /* Get expected output size */
    size_t expected;
    if (!snappy_uncompressed_length_raw(compressed, compressed_length, &expected))
        return SNAPPY_INVALID_INPUT;

    if (uncompressed_length) {
        if (*uncompressed_length < expected)
            return SNAPPY_BUFFER_TOO_SMALL;
        *uncompressed_length = expected;
    }

    int ret = snappy_uncompress_raw(compressed, compressed_length, uncompressed);
    return (ret == 0) ? SNAPPY_OK : SNAPPY_INVALID_INPUT;
}

snappy_status snappy_uncompressed_length(const char *compressed, size_t compressed_length,
                                          size_t *result)
{
    bool ok = snappy_uncompressed_length_raw(compressed, compressed_length, result);
    return ok ? SNAPPY_OK : SNAPPY_INVALID_INPUT;
}

snappy_status snappy_compress(const char *input, size_t input_length,
                              char *compressed, size_t *compressed_length)
{
    struct snappy_env env;
    int ret = snappy_init_env_raw(&env);
    if (ret != 0)
        return SNAPPY_INVALID_INPUT;

    ret = snappy_compress_raw(&env, input, input_length, compressed, compressed_length);
    snappy_free_env_raw(&env);
    return (ret == 0) ? SNAPPY_OK : SNAPPY_INVALID_INPUT;
}

size_t snappy_max_compressed_length(size_t source_len)
{
    return snappy_max_compressed_length_raw(source_len);
}
