using System.Collections.Concurrent;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Services;

/// <summary>进程内轻量事件总线，模块间通信的唯一通道。</summary>
public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public void Publish<T>(T message)
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
            foreach (var h in handlers.ToArray())
                ((Action<T>)h)(message);
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        var list = _handlers.GetOrAdd(typeof(T), _ => []);
        lock (list) list.Add(handler);
        return new Subscription(() =>
        {
            lock (list) list.Remove(handler);
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
