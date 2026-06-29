using System;

namespace WgSharp.Proto
{
    /// <summary>
    /// Anti-replay sliding window (RFC 6479 style) over the 64-bit transport
    /// counter. Tracks which recent counters have been seen so a replayed or
    /// badly-reordered packet is rejected. WireGuard's window is 2048 packets.
    /// </summary>
    public sealed class ReplayWindow
    {
        private const int WindowSize = 2048;
        private const int BlockBits = 64;                       // bits per ulong block
        private const int BlockCount = WindowSize / BlockBits;  // 32 blocks
        private const ulong RejectAfter = 0xFFFFFFFFFFFFFFFFUL; // counter never reaches this in practice

        private readonly ulong[] _bitmap = new ulong[BlockCount];
        private ulong _last;          // highest counter accepted so far
        private bool _empty = true;   // nothing accepted yet

        /// <summary>
        /// Check-and-update. Returns true if the counter is fresh (and records it),
        /// false if it is a replay or too old to verify.
        /// </summary>
        public bool CheckAndUpdate(ulong counter)
        {
            if (counter >= RejectAfter) return false;

            if (_empty)
            {
                _empty = false;
                _last = counter;
                SetBit(counter);
                return true;
            }

            if (counter > _last)
            {
                // advance the window, clearing bits we slide past
                ulong shift = counter - _last;
                if (shift >= WindowSize)
                {
                    Array.Clear(_bitmap, 0, _bitmap.Length);
                }
                else
                {
                    for (ulong i = _last + 1; i <= counter; i++)
                        ClearBit(i);
                }
                _last = counter;
                SetBit(counter);
                return true;
            }

            // counter <= _last: must be within the window and previously unseen
            if (_last - counter >= WindowSize) return false; // too old
            if (GetBit(counter)) return false;               // replay
            SetBit(counter);
            return true;
        }

        private int Index(ulong counter)
        {
            return (int)((counter / BlockBits) % BlockCount);
        }
        private int BitInBlock(ulong counter)
        {
            return (int)(counter % BlockBits);
        }
        private void SetBit(ulong c) { _bitmap[Index(c)] |= 1UL << BitInBlock(c); }
        private void ClearBit(ulong c) { _bitmap[Index(c)] &= ~(1UL << BitInBlock(c)); }
        private bool GetBit(ulong c) { return (_bitmap[Index(c)] & (1UL << BitInBlock(c))) != 0; }
    }
}
