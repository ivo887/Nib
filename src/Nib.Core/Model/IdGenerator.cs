using System;
using System.Threading;

namespace Nib.Core.Model;

/// <summary>
/// Unique 64-bit ids without a database round-trip: milliseconds since 2024 in the high bits, a
/// per-process random salt and a counter in the low bits. Ids from two devices editing the same synced
/// notebook practically never collide, which keeps v2 folder sync merge-friendly.
/// </summary>
public static class IdGenerator
{
    private static readonly long Epoch = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long Salt = Random.Shared.Next(0, 64);
    private static long _last;

    public static long Next()
    {
        while (true)
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Epoch;
            // 41 bits of time (≈ 70 years), 6 bits salt, 16 bits sequence.
            long candidate = (ms << 22) | (Salt << 16);
            long last = Interlocked.Read(ref _last);
            long next = candidate > last ? candidate : last + 1;
            if (Interlocked.CompareExchange(ref _last, next, last) == last) return next;
        }
    }
}
