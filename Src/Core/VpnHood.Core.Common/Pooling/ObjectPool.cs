using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace VpnHood.Core.Common.Pooling;

public class ObjectPool<T> where T : class, new()
{
    private readonly ConcurrentQueue<T> _objects = new();
    private readonly Func<T> _createFunc;
    private readonly Action<T>? _resetAction;
    private readonly int _maxSize;

    public ObjectPool(int maxSize = 100, Func<T>? createFunc = null, Action<T>? resetAction = null)
    {
        _maxSize = maxSize;
        _createFunc = createFunc ?? (() => new T());
        _resetAction = resetAction;
    }

    public T Rent()
    {
        return _objects.TryDequeue(out var item) ? item : _createFunc();
    }

    public void Return(T item)
    {
        if (item == null || _objects.Count >= _maxSize)
            return;

        _resetAction?.Invoke(item);
        _objects.Enqueue(item);
    }

    public void Clear()
    {
        while (_objects.TryDequeue(out _))
        {
            // Discard objects
        }
    }
}

public static class Pool
{
    private static readonly ConditionalWeakTable<Type, object> _pools = new();

    public static ObjectPool<T> GetPool<T>() where T : class, new()
    {
        return (ObjectPool<T>)_pools.GetValue(typeof(T), _ => new ObjectPool<T>());
    }

    public static T Rent<T>() where T : class, new()
    {
        return GetPool<T>().Rent();
    }

    public static void Return<T>(T item) where T : class, new()
    {
        GetPool<T>().Return(item);
    }
}
