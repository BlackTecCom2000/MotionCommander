using System.Buffers;

namespace Win11CopyDialog.Modules.PerformanceEngine;

/// <summary>
/// Арендованный блок памяти из пула с автоматическим возвратом при Dispose.
/// Предотвращает фрагментацию Large Object Heap (LOH) и снижает нагрузку на сборщик мусора до нуля.
/// </summary>
public sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;
    public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    public int Length { get; }
    public Memory<byte> Memory => new(Array, 0, Length);

    internal PooledBuffer(int minimumCapacity)
    {
        Length = minimumCapacity;
        _array = ArrayPool<byte>.Shared.Rent(minimumCapacity);
    }

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _array, null);
        if (arr != null)
        {
            ArrayPool<byte>.Shared.Return(arr);
        }
    }
}

/// <summary>
/// Высокопроизводительный менеджер пула буферов:
/// обеспечивает аренду буферов любого размера (64 КБ, 512 КБ, 1 МБ, 2 МБ, 4 МБ)
/// без повторных аллокаций памяти.
/// </summary>
public static class BufferPool
{
    public const int DefaultSmallFileSize = 128 * 1024;    // 128 KB
    public const int DefaultMediumBufferSize = 1024 * 1024; // 1 MB
    public const int DefaultLargeBufferSize = 2 * 1024 * 1024; // 2 MB
    public const int MaxLargeBufferSize = 4 * 1024 * 1024; // 4 MB

    /// <summary>
    /// Арендовать буфер заданного размера с безопасным возвратом через IDisposable.
    /// </summary>
    public static PooledBuffer Rent(int size)
    {
        return new PooledBuffer(size);
    }
}
