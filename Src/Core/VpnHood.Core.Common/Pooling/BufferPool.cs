using System.Buffers;
using System.Collections.Concurrent;

namespace VpnHood.Core.Common.Pooling;

public static class BufferPool
{
    private static readonly ConcurrentQueue<byte[]> _pool = new();
    private const int DefaultBufferSize = 8192;
    private const int MaxPoolSize = 1000;

    public static byte[] Rent(int minimumLength = DefaultBufferSize)
    {
        if (_pool.TryDequeue(out var buffer) && buffer.Length >= minimumLength)
            return buffer;

        return ArrayPool<byte>.Shared.Rent(Math.Max(minimumLength, DefaultBufferSize));
    }

    public static void Return(byte[] buffer, bool clearArray = false)
    {
        if (buffer == null || _pool.Count >= MaxPoolSize)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray);
            return;
        }

        if (clearArray)
            Array.Clear(buffer);

        _pool.Enqueue(buffer);
    }

    public static void Clear()
    {
        while (_pool.TryDequeue(out _))
        {
            // Just discard the buffers
        }
    }
}
