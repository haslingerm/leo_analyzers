using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LeoAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyIfOrElseClauseRequiresCommentAnalyzer : DiagnosticAnalyzer
{
    private const string Title = "Empty if/else clause";
    private const string Message = "Empty {0} clause must be removed or contain a comment";
    private static readonly DiagnosticDescriptor rule = new(
        Rules.EmptyIfOrElseClauseMustBeCommented,
        Title,
        Message,
        Rules.Categories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeIf, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext context)
    {
        var ifStmt = (IfStatementSyntax)context.Node;

        // Analyze the 'if' clause
        AnalyzeClause(context, ifStmt.Statement, isElse: false);

        // Analyze the 'else' clause (but not else-if which is another if statement)
        if (ifStmt.Else is { Statement: { } elseStmt and not IfStatementSyntax })
        {
            AnalyzeClause(context, elseStmt, isElse: true);
        }
    }

    private static void AnalyzeClause(SyntaxNodeAnalysisContext context, StatementSyntax statement, bool isElse)
    {
        // Empty block: {}
        if (statement is BlockSyntax { Statements.Count: 0 } block)
        {
            if (!ContainsComment(block))
            {
                Report(context, block, isElse);
            }
            return;
        }

        // Empty statement: a lone semicolon after if/else (if (x);)
        if (statement is EmptyStatementSyntax emptyStmt)
        {
            if (!ContainsComment(emptyStmt.SemicolonToken))
            {
                Report(context, emptyStmt, isElse);
            }
        }
    }

    private static bool ContainsComment(BlockSyntax block)
    {
        return block
            .DescendantTrivia(descendIntoTrivia: true)
            .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                      t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                      t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                      t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
    }

    private static bool ContainsComment(SyntaxToken token)
    {
        return token.LeadingTrivia.Concat(token.TrailingTrivia)
            .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                      t.IsKind(SyntaxKind.MultiLineCommentTrivia));
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, bool isElse)
    {
        var diagnostic = Diagnostic.Create(rule, node.GetLocation(), isElse ? "else" : "if");
        context.ReportDiagnostic(diagnostic);
    }
}
