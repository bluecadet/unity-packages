/*
 * ring_buffer.c — Simple triple-buffer for frame decode pipeline
 */

#include "ring_buffer.h"
#include <stdlib.h>
#include <string.h>

int ring_buffer_init(RingBuffer *rb, int slot_size)
{
    if (!rb || slot_size <= 0) return -1;

    memset(rb, 0, sizeof(RingBuffer));
    rb->slot_size = slot_size;
    rb->write_index = 0;
    rb->read_index = -1;

    for (int i = 0; i < RING_BUFFER_SLOTS; i++) {
        rb->slots[i] = (uint8_t *)malloc((size_t)slot_size);
        if (!rb->slots[i]) {
            ring_buffer_free(rb);
            return -1;
        }
        rb->frame_indices[i] = -1;
    }
    return 0;
}

void ring_buffer_free(RingBuffer *rb)
{
    if (!rb) return;
    for (int i = 0; i < RING_BUFFER_SLOTS; i++) {
        free(rb->slots[i]);
        rb->slots[i] = NULL;
    }
}

uint8_t *ring_buffer_write_ptr(RingBuffer *rb)
{
    if (!rb) return NULL;
    return rb->slots[rb->write_index];
}

void ring_buffer_commit_write(RingBuffer *rb, int frame_index)
{
    if (!rb) return;
    rb->frame_indices[rb->write_index] = frame_index;
    rb->read_index = rb->write_index;
    rb->write_index = (rb->write_index + 1) % RING_BUFFER_SLOTS;
}

uint8_t *ring_buffer_read_ptr(RingBuffer *rb)
{
    if (!rb || rb->read_index < 0) return NULL;
    return rb->slots[rb->read_index];
}

int ring_buffer_read_frame(RingBuffer *rb)
{
    if (!rb || rb->read_index < 0) return -1;
    return rb->frame_indices[rb->read_index];
}
