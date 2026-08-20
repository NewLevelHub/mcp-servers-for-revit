using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Keeps a Revit warning dialog from freezing an agent turn.
    ///
    /// Every tool reaches Revit through an ExternalEvent, which runs on Revit's UI
    /// thread. When a transaction commits with an unhandled warning, Revit puts up
    /// a modal dialog on that same thread — and during an agent turn there is
    /// nobody to click it. The turn does not fail: it *stops*. Each following tool
    /// call sits in the queue behind the dialog until its own timeout expires and
    /// reports an error that has nothing to do with what it was asked to do.
    ///
    /// That is what happened on 20.08.2026: `create_room` placed a room where one
    /// already stood, Revit raised «Несколько элементов "Помещения" в одной и той
    /// же окруженной области», and the next step — «Читаю параметры элемента» —
    /// timed out twice while the dialog waited for a click.
    ///
    /// Individual handlers can guard their own transaction with an
    /// <see cref="IFailuresPreprocessor"/>, and the ones that do keep doing it.
    /// But 43 of the 50 handlers that open a transaction never did, and every new
    /// one starts out unguarded, so the guarantee cannot live there. It lives
    /// here, once, for all of them.
    ///
    /// Two limits keep this from becoming a warning shredder:
    ///
    ///   - it is only armed while a tool call is actually running. Warnings raised
    ///     by the architect's own work in Revit are none of its business and are
    ///     left to appear normally;
    ///   - it dismisses warnings, never errors. An error means Revit could not do
    ///     the thing; silently resolving that is how a wall ends up somewhere
    ///     nobody asked for. Errors still stop the transaction, loudly.
    ///
    /// What was dismissed is recorded and handed back through
    /// <see cref="TakeDismissed"/>, so the answer can say a warning happened
    /// instead of reporting a clean run over a plan Revit was unhappy about.
    /// </summary>
    public static class AgentFailureGuard
    {
        private static int _depth;
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, int> Dismissed =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while a tool call is in flight.</summary>
        public static bool Armed => Volatile.Read(ref _depth) > 0;

        /// <summary>
        /// Arms the guard for the duration of one tool call.
        ///
        /// Counted rather than boolean: `batch_execute` runs commands through the
        /// same path recursively, and an inner call finishing must not disarm the
        /// outer one.
        /// </summary>
        public static IDisposable Arm()
        {
            Interlocked.Increment(ref _depth);
            return new Disarm();
        }

        /// <summary>
        /// Warnings dismissed since the last call, most frequent first, and clears
        /// the record.
        /// </summary>
        public static List<string> TakeDismissed()
        {
            lock (Lock)
            {
                var lines = Dismissed
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => pair.Value > 1
                        ? $"{pair.Key} (×{pair.Value})"
                        : pair.Key)
                    .ToList();

                Dismissed.Clear();
                return lines;
            }
        }

        /// <summary>
        /// Subscribed to <c>ControlledApplication.FailuresProcessing</c> at startup.
        /// </summary>
        public static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            if (!Armed || e == null)
            {
                return;
            }

            FailuresAccessor accessor;
            try
            {
                accessor = e.GetFailuresAccessor();
            }
            catch
            {
                return;
            }

            if (accessor == null)
            {
                return;
            }

            var dismissedAny = false;

            try
            {
                foreach (var failure in accessor.GetFailureMessages())
                {
                    if (failure == null || failure.GetSeverity() != FailureSeverity.Warning)
                    {
                        continue;
                    }

                    Record(Describe(failure));
                    accessor.DeleteWarning(failure);
                    dismissedAny = true;
                }
            }
            catch
            {
                // A failure accessor that refuses to be read is not worth taking the
                // transaction down for; Revit falls back to its own handling.
                return;
            }

            if (dismissedAny)
            {
                e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
            }
        }

        private static void Record(string description)
        {
            lock (Lock)
            {
                Dismissed.TryGetValue(description, out var count);
                Dismissed[description] = count + 1;
            }
        }

        private static string Describe(FailureMessageAccessor failure)
        {
            try
            {
                var text = failure.GetDescriptionText();
                return string.IsNullOrWhiteSpace(text)
                    ? failure.GetFailureDefinitionId().Guid.ToString()
                    : text.Trim();
            }
            catch
            {
                return "предупреждение Revit без текста";
            }
        }

        private sealed class Disarm : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref _depth);
                }
            }
        }
    }
}
