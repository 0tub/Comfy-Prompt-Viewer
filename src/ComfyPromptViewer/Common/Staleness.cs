using System;
using System.Threading;

namespace ComfyPromptViewer;

// The only staleness primitive: GenerationGate for work with no cancellation token, SessionGate for work
// with one. Both hand out a stamp that checks itself, so a caller cannot hold a generation without the
// means to test it. Do not reintroduce a bare int counter or a separately-owned token.

internal sealed class GenerationGate
{
    private int _generation;

    // Joins the running generation rather than superseding it.
    public Generation Current => new(this, Volatile.Read(ref _generation));

    // Supersedes every stamp handed out earlier.
    public Generation Begin()
    {
        return new Generation(this, Interlocked.Increment(ref _generation));
    }

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

    // One step, so no caller can observe a bumped generation with the old token still live.
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

    // Joins the session already in flight, e.g. watcher additions joining the active metadata scan.
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

    // For callers that carried only the integer across a boundary; a bare generation has no token to test.
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
