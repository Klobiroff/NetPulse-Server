using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPulse.Server.Core;

public enum PostResult
{
    Accepted,
    Full,
    Closed
}

public sealed class FiberClosedException : Exception
{
    public FiberClosedException(string name)
        : base($"Fiber '{name}' is closed")
    {
    }
}

public sealed class FiberSynchronizationContext : SynchronizationContext
{
    private readonly Fiber _fiber;

    public FiberSynchronizationContext(Fiber fiber) => _fiber = fiber;

    public override void Post(SendOrPostCallback callback, object? state) => _fiber.PostContinuation(callback, state);

    public override void Send(SendOrPostCallback callback, object? state) =>
        throw new NotSupportedException("Synchronous Send onto a fiber would block; use InvokeAsync");

    public override SynchronizationContext CreateCopy() => this;
}

public sealed class Fiber : IAsyncDisposable
{
    private readonly string _name;
    private readonly ILogger _logger;
    private readonly int _capacity;
    private readonly FiberSynchronizationContext _context;
    private readonly Channel<WorkItem> _works = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _continuations = new();
    private readonly ConcurrentDictionary<object, int> _byOwner = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _external;
    private volatile bool _closing;
    private Task? _current;

    public Fiber(string name, ILogger logger, int capacity = 4096)
    {
        _name = name;
        _logger = logger;
        _capacity = capacity;
        _context = new FiberSynchronizationContext(this);
        _ = Task.Run(RunAsync);
    }

    public bool IsCurrent => ReferenceEquals(SynchronizationContext.Current, _context);

    public int PendingCount => Volatile.Read(ref _external);

    public Task Completion => _completed.Task;

    public PostResult TryPost(Func<ValueTask> work, object owner, Action? release, out object? heaviestOwner)
    {
        heaviestOwner = null;

        if (_closing)
        {
            return PostResult.Closed;
        }

        if (Volatile.Read(ref _external) >= _capacity)
        {
            heaviestOwner = FindHeaviestOwner();
            return PostResult.Full;
        }

        return Enqueue(new WorkItem(work, owner, release)) ? PostResult.Accepted : PostResult.Closed;
    }

    public bool Post(Func<ValueTask> work) => !_closing && Enqueue(new WorkItem(work, null, null));

    public bool Post(Action action) => Post(() =>
    {
        action();
        return ValueTask.CompletedTask;
    });

    public void RunOrPost(Action action)
    {
        if (IsCurrent)
        {
            action();
            return;
        }

        Post(action);
    }

    public Task<T> InvokeAsync<T>(Func<ValueTask<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var accepted = Post(async () =>
        {
            try
            {
                completion.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!accepted)
        {
            completion.TrySetException(new FiberClosedException(_name));
        }

        return completion.Task;
    }

    public Task InvokeAsync(Func<ValueTask> work) => InvokeAsync(async () =>
    {
        await work();
        return true;
    });

    public void Complete()
    {
        _closing = true;
        _signal.Release();
    }

    public async ValueTask DisposeAsync() => await DisposeAsync(TimeSpan.FromSeconds(5));

    public async ValueTask DisposeAsync(TimeSpan timeout)
    {
        Complete();

        try
        {
            await _completed.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Fiber {Fiber} did not drain within {Timeout}", _name, timeout);
        }
    }

    internal void PostContinuation(SendOrPostCallback callback, object? state)
    {
        _continuations.Enqueue((callback, state));
        _signal.Release();
    }

    private object? FindHeaviestOwner()
    {
        object? heaviest = null;
        var max = 0;

        foreach (var pair in _byOwner)
        {
            if (pair.Value > max)
            {
                max = pair.Value;
                heaviest = pair.Key;
            }
        }

        return heaviest;
    }

    private bool Enqueue(WorkItem item)
    {
        Track(item);

        if (!_works.Writer.TryWrite(item))
        {
            Untrack(item, invokeRelease: false);
            return false;
        }

        _signal.Release();
        return true;
    }

    private void Track(WorkItem item)
    {
        if (item.Owner is null)
        {
            return;
        }

        Interlocked.Increment(ref _external);
        _byOwner.AddOrUpdate(item.Owner, 1, static (_, count) => count + 1);
    }

    private void Untrack(WorkItem item, bool invokeRelease)
    {
        if (item.Owner is null)
        {
            return;
        }

        Interlocked.Decrement(ref _external);
        var remaining = _byOwner.AddOrUpdate(item.Owner, 0, static (_, count) => count - 1);

        if (remaining <= 0)
        {
            _byOwner.TryRemove(new KeyValuePair<object, int>(item.Owner, remaining));
        }

        if (invokeRelease)
        {
            item.Release?.Invoke();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync().ConfigureAwait(false);
                DrainContinuations();

                if (_current is { IsCompleted: false })
                {
                    continue;
                }

                _current = null;

                if (_works.Reader.TryRead(out var item))
                {
                    _current = StartWork(item);
                    DrainContinuations();
                    continue;
                }

                if (_closing && _continuations.IsEmpty)
                {
                    break;
                }
            }
        }
        finally
        {
            _works.Writer.TryComplete();

            while (_works.Reader.TryRead(out var dropped))
            {
                Untrack(dropped, invokeRelease: true);
            }

            _completed.TrySetResult();
        }
    }

    private Task StartWork(WorkItem item)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_context);

        try
        {
            return RunWorkAsync(item);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task RunWorkAsync(WorkItem item)
    {
        try
        {
            await item.Run();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on fiber {Fiber}", _name);
        }
        finally
        {
            Untrack(item, invokeRelease: true);
            _signal.Release();
        }
    }

    private void DrainContinuations()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_context);

        try
        {
            while (_continuations.TryDequeue(out var entry))
            {
                try
                {
                    entry.Callback(entry.State);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception in continuation on fiber {Fiber}", _name);
                }
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed record WorkItem(Func<ValueTask> Run, object? Owner, Action? Release);
}
