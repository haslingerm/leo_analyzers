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
        Rules.EmptyIfOrElseClauseRequiresComment,
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
        // Empty block {} or a lone semicolon: if (x);
        if (statement is BlockSyntax { Statements.Count: 0 } or EmptyStatementSyntax &&
            !statement.DescendantTrivia(descendIntoTrivia: true).Any(IsCommentOrInactiveCode))
        {
            var diagnostic = Diagnostic.Create(rule, statement.GetLocation(), isElse ? "else" : "if");
            context.ReportDiagnostic(diagnostic);
        }
    }

    // Code excluded by an inactive #if is not an empty clause: it is
    // compiled in other configurations.
    private static bool IsCommentOrInactiveCode(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.DisabledTextTrivia);
}
