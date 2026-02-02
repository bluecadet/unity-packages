/*
 * ring_buffer.h — Simple lock-free(ish) ring buffer for frame decode pipeline
 */
#ifndef RING_BUFFER_H
#define RING_BUFFER_H

#include <stdint.h>

#define RING_BUFFER_SLOTS 3

typedef struct {
    uint8_t *slots[RING_BUFFER_SLOTS];
    int      slot_size;
    int      write_index;  /* next slot to write into */
    int      read_index;   /* last completed slot available for reading */
    int      frame_indices[RING_BUFFER_SLOTS]; /* which frame is in each slot */
} RingBuffer;

/* Allocate ring buffer with given slot size. Returns 0 on success. */
int  ring_buffer_init(RingBuffer *rb, int slot_size);
void ring_buffer_free(RingBuffer *rb);

/* Get pointer to the next write slot. */
uint8_t *ring_buffer_write_ptr(RingBuffer *rb);

/* Mark current write slot as containing frame_index, advance write head. */
void ring_buffer_commit_write(RingBuffer *rb, int frame_index);

/* Get pointer to the latest completed read slot, or NULL if none ready. */
uint8_t *ring_buffer_read_ptr(RingBuffer *rb);

/* Get the frame index of the current read slot. Returns -1 if none. */
int ring_buffer_read_frame(RingBuffer *rb);

#endif /* RING_BUFFER_H */
