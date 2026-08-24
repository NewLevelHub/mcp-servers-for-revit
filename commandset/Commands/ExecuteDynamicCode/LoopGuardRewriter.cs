using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode;

/// <summary>
///     REV-175: rewrites the parsed snippet so every loop checks <see cref="SandboxGuard" /> on
///     each iteration — see that class for why this replaces a real timeout/thread-abort.
/// </summary>
public sealed class LoopGuardRewriter : CSharpSyntaxRewriter
{
    private const string GuardCallText =
        "global::RevitMCPCommandSet.Commands.ExecuteDynamicCode.SandboxGuard.CheckBudget();";

    /// <summary>Returns a new tree with a guard call injected into every loop body.</summary>
    public static SyntaxTree Apply(SyntaxTree tree)
    {
        var rewritten = new LoopGuardRewriter().Visit(tree.GetRoot());
        return tree.WithRootAndOptions(rewritten, tree.Options);
    }

    public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node);
        return visited.WithStatement(GuardBody(visited.Statement));
    }

    public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node);
        return visited.WithStatement(GuardBody(visited.Statement));
    }

    public override SyntaxNode VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node);
        return visited.WithStatement(GuardBody(visited.Statement));
    }

    public override SyntaxNode VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node);
        return visited.WithStatement(GuardBody(visited.Statement));
    }

    /// <summary>Prepends the guard call, wrapping a single-statement body in a block first.</summary>
    private static StatementSyntax GuardBody(StatementSyntax body)
    {
        var guardCall = SyntaxFactory.ParseStatement(GuardCallText);

        if (body is BlockSyntax block)
            return block.WithStatements(block.Statements.Insert(0, guardCall));

        return SyntaxFactory.Block(guardCall, body);
    }
}
