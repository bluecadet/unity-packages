using System;
using System.Runtime.InteropServices;

namespace Bluecadet.Hap
{
    internal sealed class HapFrameRingBuffer : IDisposable
    {
        const int SlotCount = 3;

        readonly IntPtr[] _slots;
        readonly int[] _frameIndices;
        readonly int _slotSize;
        int _writeIndex;
        int _readIndex = -1;

        public int SlotSize => _slotSize;

        public HapFrameRingBuffer(int slotSize)
        {
            _slotSize = slotSize;
            _slots = new IntPtr[SlotCount];
            _frameIndices = new int[SlotCount];

            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = Marshal.AllocHGlobal(slotSize);
                _frameIndices[i] = -1;
            }
        }

        public IntPtr WritePtr => _slots[_writeIndex];

        public void CommitWrite(int frameIndex)
        {
            _frameIndices[_writeIndex] = frameIndex;
            _readIndex = _writeIndex;
            _writeIndex = (_writeIndex + 1) % SlotCount;
        }

        public IntPtr ReadPtr => _readIndex >= 0 ? _slots[_readIndex] : IntPtr.Zero;

        public int ReadFrame => _readIndex >= 0 ? _frameIndices[_readIndex] : -1;

        public void Dispose()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_slots[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_slots[i]);
                    _slots[i] = IntPtr.Zero;
                }
            }
        }
    }
}
