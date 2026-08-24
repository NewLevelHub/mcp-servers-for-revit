namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode;

/// <summary>
///     REV-175: thrown by <see cref="SandboxGuard" /> when a loop in the AI-generated snippet
///     runs past its time budget. Caught by <see cref="ExecuteCodeEventHandler" />, which rolls
///     the transaction back and reports it as a normal (non-crashing) failure.
/// </summary>
public sealed class SandboxTimeoutException : Exception
{
    public SandboxTimeoutException(TimeSpan limit)
        : base($"Sandbox timeout after {limit.TotalSeconds:0.#}s")
    {
        Limit = limit;
    }

    public TimeSpan Limit { get; }
}

/// <summary>
///     REV-175: thrown once the post-run <see cref="ChangeIntent" /> diff shows more created +
///     deleted elements than the configured budget — the safety net named in the ticket for a
///     runaway bulk delete/create. Note this can only be checked after the snippet returns (see
///     <see cref="ChangeIntentRecorder" /> remarks for why), so the transaction that held the
///     attempt is always rolled back in response — nothing from it reaches the document.
/// </summary>
public sealed class SandboxLimitExceededException : Exception
{
    public SandboxLimitExceededException(int touched, int max)
        : base($"Sandbox touched-element limit exceeded: {touched} > {max}")
    {
        Touched = touched;
        Max = max;
    }

    public int Touched { get; }
    public int Max { get; }
}

/// <summary>
///     REV-175: thrown by <see cref="SandboxGuard" /> when a loop runs more iterations than its
///     budget, independent of the wall-clock timeout. Catches a loop that is fast per-iteration
///     but touches far more elements than the count limit would ever let commit anyway — e.g. a
///     bulk parameter edit across the whole model, which <see cref="ChangeIntentRecorder" />
///     itself cannot see (it only tracks creation/deletion).
/// </summary>
public sealed class SandboxLoopIterationLimitException : Exception
{
    public SandboxLoopIterationLimitException(long iterations, long max)
        : base($"Sandbox loop-iteration limit exceeded: {iterations} > {max}")
    {
        Iterations = iterations;
        Max = max;
    }

    public long Iterations { get; }
    public long Max { get; }
}

/// <summary>
///     REV-175: thrown by <see cref="DangerousApiGuard" /> when the snippet references a banned
///     API (filesystem, network, process spawning) before it ever gets compiled to IL.
/// </summary>
public sealed class SandboxSecurityException : Exception
{
    public SandboxSecurityException(string symbolName)
        : base($"Sandbox blocked a banned API: {symbolName}")
    {
        SymbolName = symbolName;
    }

    public string SymbolName { get; }
}
