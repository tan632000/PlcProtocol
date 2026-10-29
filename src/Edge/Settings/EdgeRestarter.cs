namespace IndustrialIot.Edge.Settings;

public enum ApplyState
{
    /// <summary>The running inner host was built from the settings named by the status revision.</summary>
    Applied,

    /// <summary>A save was accepted and the inner host is being rebuilt.</summary>
    Applying,

    /// <summary>The saved settings did not start; the previous settings were restored and run.</summary>
    RolledBack,
}

/// <summary>
/// <see cref="Revision"/>: the settings revision the running inner host was built from.
/// <see cref="ApplyId"/>/<see cref="State"/>/<see cref="Error"/> describe the latest apply; <see cref="Error"/> is a
/// fixed message, never exception text.
/// </summary>
public sealed record ApplyStatus(string Revision, string? ApplyId, ApplyState State, string? Error);

/// <summary>An accepted save: the new settings file bytes, or null to delete the file (back to appsettings).</summary>
public sealed record ApplyRequest(string ApplyId, byte[]? Bytes);

/// <summary>
/// Hand-off between the settings API and <see cref="EdgeSupervisor"/>, shared by every inner host. A request only
/// queues: the supervisor acts after <see cref="Release"/> (the HTTP response that carried it has completed), because
/// stopping the host that serves that very request from inside the handler would deadlock. One apply at a time.
/// </summary>
public sealed class EdgeRestarter
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _queued = new(0);
    private Pending? _pending;
    private ApplyStatus _status = new(EdgeSettingsFile.NoFileRevision, null, ApplyState.Applied, null);

    public ApplyStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>True while a request waits or an apply runs; a new request is refused then.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Queues an apply of <paramref name="bytes"/> (null deletes the settings file).</summary>
    /// <returns>False when another apply is queued or running.</returns>
    public bool TryRequest(byte[]? bytes, out string applyId)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                applyId = "";
                return false;
            }

            applyId = Guid.NewGuid().ToString("N");
            _pending = new Pending(new ApplyRequest(applyId, bytes is null ? null : (byte[])bytes.Clone()));
            _status = _status with { ApplyId = applyId, State = ApplyState.Applying, Error = null };
        }

        _queued.Release();
        return true;
    }

    /// <summary>Lets the supervisor go ahead with <paramref name="applyId"/> (call once its response has completed).</summary>
    public void Release(string applyId)
    {
        lock (_gate)
        {
            if (_pending?.Request.ApplyId == applyId)
            {
                _pending.Released.TrySetResult();
            }
        }
    }

    /// <summary>Waits for a request, then for its release; a missing release only delays it by <paramref name="releaseTimeout"/>.</summary>
    internal async Task<ApplyRequest> WaitForRequestAsync(TimeSpan releaseTimeout, CancellationToken ct)
    {
        await _queued.WaitAsync(ct).ConfigureAwait(false);
        Pending pending;
        lock (_gate)
        {
            pending = _pending!;
        }

        try
        {
            await pending.Released.Task.WaitAsync(releaseTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The response never reported completion (client gone): apply anyway.
        }

        return pending.Request;
    }

    /// <summary>Records the revision of the inner host started without a request (process start).</summary>
    internal void Initialize(string revision)
    {
        lock (_gate)
        {
            _status = _status with { Revision = revision };
        }
    }

    /// <summary>Ends the current apply with its final state and frees the slot for the next request.</summary>
    internal void Complete(string applyId, ApplyState state, string revision, string? error)
    {
        lock (_gate)
        {
            _status = new ApplyStatus(revision, applyId, state, error);
            _pending = null;
        }
    }

    private sealed class Pending(ApplyRequest request)
    {
        public ApplyRequest Request { get; } = request;

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
