using System.Buffers;

namespace MotionCommander.Core.Streaming;

public sealed class PooledBuffer : IDisposable
{
    private byte[]? _buffer;
    private readonly ArrayPool<byte> _pool;
    private bool _disposed;

    public byte[] Data => _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    public int Length => _buffer?.Length ?? 0;

    public PooledBuffer(int minCapacity, ArrayPool<byte>? pool = null)
    {
        _pool = pool ?? ArrayPool<byte>.Shared;
        _buffer = _pool.Rent(minCapacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer != null)
        {
            _pool.Return(_buffer);
            _buffer = null;
        }
    }
}

public static class BufferPool
{
    public const int DefaultBufferSize = 1024 * 1024; // 1 MB
    public const int MaxBufferSize = 4 * 1024 * 1024; // 4 MB
    public const int SmallFileBufferSize = 256 * 1024; // 256 KB

    public static PooledBuffer Rent(int size) => new(size);
}
