using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DebugToolbox.Runtime;

internal sealed class MainThreadDispatcher
{
    private sealed class FrameWaiter
    {
        public TaskCompletionSource<bool> Completion { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly object _frameLock = new();
    private List<FrameWaiter> _nextFrame = new();

    public void Post(Action action)
    {
        _actions.Enqueue(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    public Task NextFrame(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>();
        lock (_frameLock)
        {
            _nextFrame.Add(new FrameWaiter
            {
                Completion = completion,
                CancellationToken = cancellationToken
            });
        }
        return completion.Task;
    }

    public void Update()
    {
        List<FrameWaiter> due;
        lock (_frameLock)
        {
            due = _nextFrame;
            _nextFrame = new List<FrameWaiter>();
        }

        while (_actions.TryDequeue(out var action))
        {
            action();
        }

        foreach (var waiter in due)
        {
            if (waiter.CancellationToken.IsCancellationRequested)
            {
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            }
            else
            {
                waiter.Completion.TrySetResult(true);
            }
        }
    }
}
