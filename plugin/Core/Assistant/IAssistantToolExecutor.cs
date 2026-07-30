namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Executes an assistant tool by name. Production uses Revit JSON-RPC;
    /// golden dry-run substitutes a stub (REV-111).
    /// </summary>
    public interface IAssistantToolExecutor
    {
        string Execute(string toolName, string argsJson);
    }
}
