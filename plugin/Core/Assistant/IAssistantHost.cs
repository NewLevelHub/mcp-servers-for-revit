using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Shared surface for in-Revit chat backends: legacy OpenAI loop or Cursor SDK bridge (REV-155).
    /// </summary>
    public interface IAssistantHost
    {
        event Action<string> StatusChanged;
        event Func<PendingToolConfirmation, Task<bool>> ConfirmationRequested;
        event Func<PendingAskUser, Task<AskUserAnswer>> AskUserRequested;
        event Action HistoryTrimmed;
        event Action<HistoryBudget> HistoryBudgetChanged;
        event Action<AgentPlanSnapshot> PlanChanged;
        event Action<string> ModelEscalated;
        event Action<ToolStepEvent> ToolStepChanged;
        event Action<string> ReplyDelta;

        void ClearHistory();
        HistoryBudget GetHistoryBudget();

        Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            CancellationToken cancellationToken);

        Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            IList<ChatAttachment> attachments,
            CancellationToken cancellationToken,
            string turnId = null,
            IReadOnlyList<string> toolProfiles = null);
    }

    public static class AssistantHostFactory
    {
        public static IAssistantHost Create(ServiceSettings settings)
        {
            // Cursor-only product path (REV-155). LocalAgentHost stays in the tree
            // because the golden/prompt test suites build on it, but nothing reaches it.
            return new CursorBridgeAgentHost(settings ?? new ServiceSettings());
        }
    }
}
