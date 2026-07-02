namespace DotRocks.Data;

/// <summary>
/// Tracks the cancellation source of the operation currently executing on an ADO.NET surface
/// object (a command or a batch), enforcing the single-active-operation rule and giving
/// <c>Cancel()</c> a race-free handle to the in-flight operation.
/// </summary>
internal sealed class ActiveOperationGate
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _active;

    public void Set(CancellationTokenSource operationCancellation, string conflictMessage)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(conflictMessage);
            }

            _active = operationCancellation;
        }
    }

    public void Clear(CancellationTokenSource operationCancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, operationCancellation))
            {
                _active = null;
            }
        }
    }

    /// <summary>
    /// Cancels the in-flight operation, if any. Returns true when an operation was active so the
    /// caller can also abort its connection.
    /// </summary>
    public bool TryCancelActiveOperation()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            active = _active;
        }

        if (active is null)
        {
            return false;
        }

        active.Cancel();
        return true;
    }
}

/// <summary>
/// Owns the cancellation plumbing for one command or batch execution: the operation's own
/// cancellation source (the <c>Cancel()</c> target), the optional timeout source, and the linked
/// source combining both with the caller's token. Registers with the gate on creation and
/// unregisters (then disposes the sources) on dispose.
/// </summary>
internal sealed class ActiveOperationScope : IDisposable
{
    private readonly ActiveOperationGate _gate;
    private readonly CancellationTokenSource _operationCancellation;
    private readonly CancellationTokenSource? _timeoutCancellation;
    private readonly CancellationTokenSource _linkedCancellation;
    private readonly CancellationToken _externalToken;

    public ActiveOperationScope(
        ActiveOperationGate gate,
        int timeoutSeconds,
        string conflictMessage,
        CancellationToken externalToken
    )
    {
        _gate = gate;
        _externalToken = externalToken;
        _operationCancellation = new CancellationTokenSource();
        // A timeout of zero means "no timeout" per the ADO.NET CommandTimeout convention.
        _timeoutCancellation =
            timeoutSeconds == 0
                ? null
                : new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        _linkedCancellation = _timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                externalToken,
                _operationCancellation.Token
            )
            : CancellationTokenSource.CreateLinkedTokenSource(
                externalToken,
                _operationCancellation.Token,
                _timeoutCancellation.Token
            );

        try
        {
            gate.Set(_operationCancellation, conflictMessage);
        }
        catch
        {
            DisposeSources();
            throw;
        }
    }

    /// <summary>The token the operation observes (caller token + timeout + <c>Cancel()</c>).</summary>
    public CancellationToken Token => _linkedCancellation.Token;

    /// <summary>
    /// The operation's own cancellation token — the <c>Cancel()</c> target — used to attribute a
    /// rethrown <see cref="OperationCanceledException"/> to <c>Cancel()</c> rather than to the
    /// caller's token or the timeout.
    /// </summary>
    public CancellationToken OperationToken => _operationCancellation.Token;

    // The failure is attributable to the timeout: it fired while neither the caller's token nor
    // Cancel() did.
    public bool IsTimeout =>
        _timeoutCancellation?.IsCancellationRequested == true
        && !_externalToken.IsCancellationRequested
        && !_operationCancellation.IsCancellationRequested;

    // The failure is attributable to Cancel(): the operation source fired and the caller's token
    // did not.
    public bool IsCanceledByCancelMethod =>
        _operationCancellation.IsCancellationRequested && !_externalToken.IsCancellationRequested;

    public void Dispose()
    {
        _gate.Clear(_operationCancellation);
        DisposeSources();
    }

    private void DisposeSources()
    {
        _linkedCancellation.Dispose();
        _timeoutCancellation?.Dispose();
        _operationCancellation.Dispose();
    }
}
