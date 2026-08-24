using Microsoft.CodeAnalysis;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode;

/// <summary>
///     REV-175: rejects AI-generated code that references filesystem, network, or
///     process-spawning APIs, before the snippet is ever emitted to IL.
///     <para>
///     Deliberately narrow: it is a symbol-resolution check against a short denylist, not a
///     general capability sandbox. It does not attempt to block Revit's own file-producing APIs
///     (e.g. schedule/export commands take a path string, not a <c>System.IO</c> type) — those
///     are Revit-mediated, not the raw escape hatch this guards against. "Deletion outside an
///     explicit selection" is not statically checkable here either; that risk is bounded instead
///     by <see cref="ChangeIntentRecorder" />'s touched-element limit and by requiring a trial
///     run before anything commits.
///     </para>
/// </summary>
public static class DangerousApiGuard
{
    // Namespace prefixes: anything whose containing namespace starts with one of these is banned,
    // using-directive or fully-qualified reference alike.
    private static readonly string[] BannedNamespacePrefixes =
    {
        "System.IO",
        "System.Net",
        "Microsoft.Win32",
    };

    // Specific types worth banning even though their namespace (System.Diagnostics) is mostly
    // fine (Stopwatch lives there too) — Process is an arbitrary-code-execution escape hatch.
    private static readonly string[] BannedFullTypeNames =
    {
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
    };

    /// <summary>Throws <see cref="SandboxSecurityException" /> on the first banned reference found.</summary>
    public static void Validate(SemanticModel model, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol == null)
                continue;

            var containingType = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            var fullTypeName = containingType?.ToDisplayString();
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            var banned =
                (fullTypeName != null && Array.Exists(BannedFullTypeNames,
                    b => fullTypeName.StartsWith(b, StringComparison.Ordinal))) ||
                Array.Exists(BannedNamespacePrefixes, p => ns.StartsWith(p, StringComparison.Ordinal));

            if (banned)
                throw new SandboxSecurityException(fullTypeName ?? symbol.ToDisplayString());
        }
    }
}
