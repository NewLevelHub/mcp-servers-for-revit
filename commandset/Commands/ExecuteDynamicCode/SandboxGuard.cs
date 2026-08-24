using System.Diagnostics;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode;

/// <summary>
///     REV-175: enforces a wall-clock budget and an iteration budget for AI-generated code
///     without a thread to abort.
///     <para>
///     The snippet runs synchronously on Revit's UI thread inside an ExternalEvent — there is no
///     worker thread to interrupt, and <c>Thread.Abort</c> does not exist on the .NET 8 targets
///     this project also ships (Revit 2025/2026). So a tight <c>while (true)</c> cannot be
///     stopped from the outside; it has to police itself. <see cref="LoopGuardRewriter" />
///     injects a call to <see cref="CheckBudget" /> at the top of every loop body the compiler
///     sees, so the loop itself throws once it has run past its budget.
///     </para>
///     <para>
///     Two budgets, not one: the timeout catches a loop that runs long, and the iteration count
///     catches one that is fast per-iteration but does a lot of (cheap-looking) work — e.g. a
///     bulk parameter edit across thousands of elements, which finishes in a couple of seconds
///     and would sail under any reasonable timeout, but which <see cref="ChangeIntentRecorder" />
///     also can't see (it only tracks creation/deletion, not in-place edits — see its remarks).
///     The iteration cap is deliberately generous (default: hundreds of thousands) so it never
///     fires on a normal loop over a normal-sized selection; it exists only to stop something
///     that iterates over the whole model many times over.
///     </para>
///     <para>
///     This only bounds loops — a single slow non-looping API call is not caught. That matches
///     the acceptance criterion ("бесконечный цикл"); it is not a general CPU/time sandbox.
///     </para>
/// </summary>
public static class SandboxGuard
{
    [ThreadStatic] private static Stopwatch _stopwatch;
    [ThreadStatic] private static TimeSpan _timeLimit;
    [ThreadStatic] private static long _iterations;
    [ThreadStatic] private static long _iterationLimit;
    [ThreadStatic] private static bool _active;

    /// <summary>Call once, right before invoking the compiled snippet.</summary>
    public static void Begin(TimeSpan timeLimit, long iterationLimit)
    {
        _timeLimit = timeLimit;
        _stopwatch = Stopwatch.StartNew();
        _iterations = 0;
        _iterationLimit = iterationLimit;
        _active = true;
    }

    /// <summary>Call in a finally block after the snippet returns, throws, or is stopped.</summary>
    public static void End()
    {
        _active = false;
        _stopwatch = null;
    }

    /// <summary>
    ///     Injected by <see cref="LoopGuardRewriter" /> at the top of every for/foreach/while/do
    ///     loop body in the compiled snippet. Throws <see cref="SandboxTimeoutException" /> or
    ///     <see cref="SandboxLoopIterationLimitException" /> once a budget set in
    ///     <see cref="Begin" /> is exceeded.
    /// </summary>
    public static void CheckBudget()
    {
        if (!_active)
            return;

        if (_stopwatch != null && _stopwatch.Elapsed > _timeLimit)
            throw new SandboxTimeoutException(_timeLimit);

        if (++_iterations > _iterationLimit)
            throw new SandboxLoopIterationLimitException(_iterations, _iterationLimit);
    }
}
