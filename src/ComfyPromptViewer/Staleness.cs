using System;
using System.Threading;

namespace ComfyPromptViewer;

// Every async result in this app has to be rejected when something newer superseded it. That pattern was
// re-derived per subsystem as a bare int counter, and almost every stale-result bug was some path
// forgetting to bump or forgetting to check. These two types are the only staleness primitive:
//
//   GenerationGate - a counter for work that has no cancellation token (UI animations, queued restores).
//   SessionGate    - a counter paired with a CancellationTokenSource, for background work.
//
// Both hand out a stamp that knows how to check itself, so a caller cannot hold a generation without also
// holding the way to test it. Never store the raw integer alongside a separately-owned token again.

internal sealed class GenerationGate
{
    private int _generation;

    // A stamp for the generation already running, for work that joins it rather than superseding it.
    public Generation Current => new(this, Volatile.Read(ref _generation));

    // Starts a new generation and returns its stamp. Every stamp handed out earlier is now stale.
    public Generation Begin()
    {
        return new Generation(this, Interlocked.Increment(ref _generation));
    }

    // Invalidates every outstanding stamp without starting work of its own.
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
    }

    internal bool IsCurrent(int generation)
    {
        return Volatile.Read(ref _generation) == generation;
    }
}

internal readonly record struct Generation(GenerationGate? Gate, int Value)
{
    public bool IsCurrent => Gate is not null && Gate.IsCurrent(Value);
    public bool IsStale => !IsCurrent;
}

internal sealed class SessionGate
{
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public int Generation
    {
        get
        {
            lock (_stateLock)
            {
                return _generation;
            }
        }
    }

    public CancellationToken? CurrentToken
    {
        get
        {
            lock (_stateLock)
            {
                return _cancellation?.Token;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_stateLock)
            {
                return _cancellation is { IsCancellationRequested: false };
            }
        }
    }

    // Cancels the previous session and opens a new one in a single step, so there is no window where a
    // caller can observe a bumped generation with the old token still live.
    public Session Restart()
    {
        CancellationTokenSource? previous;
        Session session;
        lock (_stateLock)
        {
            previous = _cancellation;
            _cancellation = new CancellationTokenSource();
            session = new Session(this, _cancellation.Token, ++_generation);
        }

        CancelAndDispose(previous);
        return session;
    }

    // A stamp for the session already in flight. Used by work that joins an existing session (watcher
    // additions joining the active metadata scan) rather than superseding it.
    public Session Snapshot()
    {
        lock (_stateLock)
        {
            return new Session(this, _cancellation?.Token ?? CancellationToken.None, _generation);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
        }

        CancelAndDispose(cancellation);
    }

    // For callers that only carried the integer across a boundary (a posted UI callback). Requires an
    // active session, because a bare generation carries no token of its own to test.
    public bool IsCurrent(int generation)
    {
        lock (_stateLock)
        {
            return generation == _generation && _cancellation is { IsCancellationRequested: false };
        }
    }

    internal bool IsCurrent(int generation, CancellationToken token)
    {
        lock (_stateLock)
        {
            return generation == _generation && !token.IsCancellationRequested;
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
    }
}

internal readonly record struct Session(SessionGate? Gate, CancellationToken Token, int Generation)
{
    public bool IsCurrent => Gate is not null && Gate.IsCurrent(Generation, Token);
    public bool IsStale => !IsCurrent;
}
