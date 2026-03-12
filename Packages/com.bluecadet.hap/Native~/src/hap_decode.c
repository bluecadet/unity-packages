/*
 * hap_decode.c — HAP frame decoder with thread pool for parallel chunk decompression
 */

#include "hap_decode.h"
#include "hap.h"
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#include <pthread.h>
#endif

/* ── Thread pool ── */

#ifdef _WIN32

typedef struct {
    HANDLE         *threads;
    int             count;
    CRITICAL_SECTION cs;
    CONDITION_VARIABLE work_cv;
    CONDITION_VARIABLE done_cv;
    HapDecodeWorkFunction work_func;
    void           *work_info;
    unsigned int    work_count;
    unsigned int    work_next;
    unsigned int    work_completed;
    int             shutdown;
} ThreadPool;

static DWORD WINAPI thread_worker_win(LPVOID arg)
{
    ThreadPool *pool = (ThreadPool *)arg;
    EnterCriticalSection(&pool->cs);
    while (!pool->shutdown) {
        if (pool->work_next < pool->work_count) {
            unsigned int index = pool->work_next++;
            LeaveCriticalSection(&pool->cs);
            pool->work_func(pool->work_info, index);
            EnterCriticalSection(&pool->cs);
            pool->work_completed++;
            if (pool->work_completed == pool->work_count)
                WakeConditionVariable(&pool->done_cv);
        } else {
            SleepConditionVariableCS(&pool->work_cv, &pool->cs, INFINITE);
        }
    }
    LeaveCriticalSection(&pool->cs);
    return 0;
}

static void pool_destroy(ThreadPool *pool); /* forward declaration */

static ThreadPool *pool_create(int count)
{
    ThreadPool *pool = (ThreadPool *)calloc(1, sizeof(ThreadPool));
    if (!pool) return NULL;
    pool->count = count;
    InitializeCriticalSection(&pool->cs);
    InitializeConditionVariable(&pool->work_cv);
    InitializeConditionVariable(&pool->done_cv);
    pool->threads = (HANDLE *)calloc((size_t)count, sizeof(HANDLE));
    if (!pool->threads) {
        DeleteCriticalSection(&pool->cs);
        free(pool);
        return NULL;
    }
    for (int i = 0; i < count; i++) {
        pool->threads[i] = CreateThread(NULL, 0, thread_worker_win, pool, 0, NULL);
        if (!pool->threads[i]) {
            /* Shut down any threads already started before failing. */
            pool->count = i;
            pool_destroy(pool);
            return NULL;
        }
    }
    return pool;
}

static void pool_destroy(ThreadPool *pool)
{
    if (!pool) return;
    EnterCriticalSection(&pool->cs);
    pool->shutdown = 1;
    WakeAllConditionVariable(&pool->work_cv);
    LeaveCriticalSection(&pool->cs);
    for (int i = 0; i < pool->count; i++) {
        WaitForSingleObject(pool->threads[i], INFINITE);
        CloseHandle(pool->threads[i]);
    }
    free(pool->threads);
    DeleteCriticalSection(&pool->cs);
    free(pool);
}

static void pool_dispatch(ThreadPool *pool, HapDecodeWorkFunction func,
                          void *info, unsigned int count)
{
    EnterCriticalSection(&pool->cs);
    pool->work_func = func;
    pool->work_info = info;
    pool->work_count = count;
    pool->work_next = 0;
    pool->work_completed = 0;
    WakeAllConditionVariable(&pool->work_cv);
    while (pool->work_completed < pool->work_count)
        SleepConditionVariableCS(&pool->done_cv, &pool->cs, INFINITE);
    LeaveCriticalSection(&pool->cs);
}

static int get_cpu_count(void)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    return (int)si.dwNumberOfProcessors;
}

#else /* POSIX */

typedef struct {
    pthread_t      *threads;
    int             count;
    pthread_mutex_t mutex;
    pthread_cond_t  work_cv;
    pthread_cond_t  done_cv;
    HapDecodeWorkFunction work_func;
    void           *work_info;
    unsigned int    work_count;
    unsigned int    work_next;
    unsigned int    work_completed;
    int             shutdown;
} ThreadPool;

static void *thread_worker(void *arg)
{
    ThreadPool *pool = (ThreadPool *)arg;
    pthread_mutex_lock(&pool->mutex);
    while (!pool->shutdown) {
        if (pool->work_next < pool->work_count) {
            unsigned int index = pool->work_next++;
            pthread_mutex_unlock(&pool->mutex);
            pool->work_func(pool->work_info, index);
            pthread_mutex_lock(&pool->mutex);
            pool->work_completed++;
            if (pool->work_completed == pool->work_count)
                pthread_cond_signal(&pool->done_cv);
        } else {
            pthread_cond_wait(&pool->work_cv, &pool->mutex);
        }
    }
    pthread_mutex_unlock(&pool->mutex);
    return NULL;
}

static void pool_destroy(ThreadPool *pool);  /* forward declaration for error path in pool_create */

static ThreadPool *pool_create(int count)
{
    ThreadPool *pool = (ThreadPool *)calloc(1, sizeof(ThreadPool));
    if (!pool) return NULL;
    pool->count = count;
    pthread_mutex_init(&pool->mutex, NULL);
    pthread_cond_init(&pool->work_cv, NULL);
    pthread_cond_init(&pool->done_cv, NULL);
    pool->threads = (pthread_t *)calloc((size_t)count, sizeof(pthread_t));
    if (!pool->threads) {
        pthread_mutex_destroy(&pool->mutex);
        pthread_cond_destroy(&pool->work_cv);
        pthread_cond_destroy(&pool->done_cv);
        free(pool);
        return NULL;
    }
    for (int i = 0; i < count; i++) {
        if (pthread_create(&pool->threads[i], NULL, thread_worker, pool) != 0) {
            /* Shut down any threads already started before failing. */
            pool->count = i;
            pool_destroy(pool);
            return NULL;
        }
    }
    return pool;
}

static void pool_destroy(ThreadPool *pool)
{
    if (!pool) return;
    pthread_mutex_lock(&pool->mutex);
    pool->shutdown = 1;
    pthread_cond_broadcast(&pool->work_cv);
    pthread_mutex_unlock(&pool->mutex);
    for (int i = 0; i < pool->count; i++)
        pthread_join(pool->threads[i], NULL);
    free(pool->threads);
    pthread_mutex_destroy(&pool->mutex);
    pthread_cond_destroy(&pool->work_cv);
    pthread_cond_destroy(&pool->done_cv);
    free(pool);
}

static void pool_dispatch(ThreadPool *pool, HapDecodeWorkFunction func,
                          void *info, unsigned int count)
{
    pthread_mutex_lock(&pool->mutex);
    pool->work_func = func;
    pool->work_info = info;
    pool->work_count = count;
    pool->work_next = 0;
    pool->work_completed = 0;
    pthread_cond_broadcast(&pool->work_cv);
    while (pool->work_completed < pool->work_count)
        pthread_cond_wait(&pool->done_cv, &pool->mutex);
    pthread_mutex_unlock(&pool->mutex);
}

static int get_cpu_count(void)
{
    int n = (int)sysconf(_SC_NPROCESSORS_ONLN);
    return n > 0 ? n : 4;
}

#endif /* _WIN32 / POSIX */

/* ── HAP decode callback ── */

static void hap_parallel_decode(HapDecodeWorkFunction func,
                                void *info,
                                unsigned int count,
                                void *context)
{
    ThreadPool *pool = (ThreadPool *)context;
    if (count <= 1 || !pool) {
        for (unsigned int i = 0; i < count; i++)
            func(info, i);
        return;
    }
    pool_dispatch(pool, func, info, count);
}

/* ── Public API ── */

struct HapDecoder {
    ThreadPool *pool;
    int         thread_count;
};

HapDecoder *hap_decoder_create(int thread_count)
{
    HapDecoder *dec = (HapDecoder *)calloc(1, sizeof(HapDecoder));
    if (!dec) return NULL;

    if (thread_count <= 0) thread_count = get_cpu_count();
    if (thread_count > 64)  thread_count = 64;

    dec->thread_count = thread_count;
    dec->pool = pool_create(thread_count);
    return dec;
}

void hap_decoder_destroy(HapDecoder *dec)
{
    if (!dec) return;
    pool_destroy(dec->pool);
    free(dec);
}

void hap_decoder_set_thread_count(HapDecoder *dec, int count)
{
    if (!dec) return;
    if (count <= 0) count = get_cpu_count();
    if (count > 64)  count = 64;
    if (count == dec->thread_count) return;
    pool_destroy(dec->pool);
    dec->thread_count = count;
    dec->pool = pool_create(count);
}

int hap_decoder_decode(HapDecoder *dec,
                       const uint8_t *input, int input_size,
                       uint8_t *output, int output_size,
                       int *out_texture_format)
{
    if (!dec || !input || !output || input_size <= 0 || output_size <= 0)
        return -1;

    unsigned int format = 0;
    unsigned long bytes_used = 0;
    unsigned int result = HapDecode(input, (unsigned long)input_size,
                                    0, /* texture index 0 */
                                    hap_parallel_decode,
                                    dec->pool,
                                    output, (unsigned long)output_size,
                                    &bytes_used, &format);

    if (result != HapResult_No_Error)
        return (int)result;

    if (out_texture_format) {
        switch (format) {
            case HapTextureFormat_RGB_DXT1:
                *out_texture_format = HAP_TEX_FORMAT_DXT1;
                break;
            case HapTextureFormat_RGBA_DXT5:
                *out_texture_format = HAP_TEX_FORMAT_DXT5;
                break;
            case HapTextureFormat_YCoCg_DXT5:
                *out_texture_format = HAP_TEX_FORMAT_YCOCG_DXT5;
                break;
            case HapTextureFormat_RGBA_BPTC_UNORM:
                *out_texture_format = HAP_TEX_FORMAT_BC7;
                break;
            default:
                *out_texture_format = HAP_TEX_FORMAT_DXT1;
                break;
        }
    }

    return 0;
}
