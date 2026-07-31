using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Clears door/window insertion warnings that otherwise pop a modal dialog
    /// and freeze the ExternalEvent (agent sees timeout / failed step).
    /// </summary>
    public sealed class SuppressOpeningWarningsPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            if (failuresAccessor == null)
                return FailureProcessingResult.Continue;

            IList<FailureMessageAccessor> messages = failuresAccessor.GetFailureMessages();
            if (messages == null || messages.Count == 0)
                return FailureProcessingResult.Continue;

            foreach (FailureMessageAccessor failure in messages)
            {
                if (failure == null)
                    continue;

                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }

                // Try to resolve non-fatal errors the same way when possible.
                try
                {
                    if (failure.HasResolutions())
                        failuresAccessor.ResolveFailure(failure);
                }
                catch
                {
                    // leave unresolved — transaction may still roll back
                }
            }

            return FailureProcessingResult.Continue;
        }
    }
}
