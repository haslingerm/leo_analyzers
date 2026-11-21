using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace LeoAnalyzers.CodeFixes;

/// <summary>
///     Fixes for LA0002. Deliberately no "add a comment" fix: the rule exists so that an empty clause is
///     either removed or explained by a human, so the fixes only offer the two removals that keep behavior:
///     <list type="bullet">
///         <item>an empty <c>else</c> is removed,</item>
///         <item>an empty <c>if</c> with a non-empty <c>else</c> is inverted: <c>if (!c) { else body }</c>.</item>
///     </list>
///     An empty <c>if</c> without <c>else</c> gets no fix: its condition may have side effects.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EmptyIfOrElseClauseCodeFixProvider))]
[Shared]
public sealed class EmptyIfOrElseClauseCodeFixProvider : CodeFixProvider
{
    internal const string RemoveElseKey = Rules.EmptyIfOrElseClauseRequiresComment + ".RemoveEmptyElse";
    internal const string InvertIfKey = Rules.EmptyIfOrElseClauseRequiresComment + ".InvertIf";

    public override ImmutableArray<string> FixableDiagnosticIds => [Rules.EmptyIfOrElseClauseRequiresComment];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not StatementSyntax statement)
            {
                continue;
            }

            if (statement.Parent is ElseClauseSyntax { Parent: IfStatementSyntax ifWithEmptyElse })
            {
                context.RegisterCodeFix(
                    CodeAction.Create("Remove empty else",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(RemoveElse(root, ifWithEmptyElse))),
                        RemoveElseKey),
                    diagnostic);
            }
            else if (statement.Parent is IfStatementSyntax ifStatement && ifStatement.Statement == statement &&
                     ifStatement.Else is { Statement: var elseStatement } && !IsEmpty(elseStatement) &&
                     await IsBooleanAsync(context.Document, ifStatement.Condition, context.CancellationToken).ConfigureAwait(false))
            {
                context.RegisterCodeFix(
                    CodeAction.Create("Invert condition and remove empty if",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(Invert(root, ifStatement, elseStatement))),
                        InvertIfKey),
                    diagnostic);
            }
        }
    }

    private static SyntaxNode RemoveElse(SyntaxNode root, IfStatementSyntax ifStatement)
    {
        var elseClause = ifStatement.Else!;
        // Comments in front of 'else' (e.g. after the closing brace) are kept.
        var keptTrivia = elseClause.ElseKeyword.LeadingTrivia.Where(IsComment).ToList();
        var newIf = ifStatement.WithElse(null);
        if (keptTrivia.Count > 0)
        {
            newIf = newIf.WithTrailingTrivia(newIf.GetTrailingTrivia().AddRange(keptTrivia.Prepend(SyntaxFactory.Space)).Add(SyntaxFactory.ElasticCarriageReturnLineFeed));
        }
        else
        {
            newIf = newIf.WithTrailingTrivia(elseClause.GetTrailingTrivia());
        }

        return root.ReplaceNode(ifStatement, newIf.WithAdditionalAnnotations(Formatter.Annotation));
    }

    private static SyntaxNode Invert(SyntaxNode root, IfStatementSyntax ifStatement, StatementSyntax elseStatement)
    {
        // An else-if becomes the body of the inverted if; a block keeps a dangling else from changing its meaning.
        var body = elseStatement is IfStatementSyntax
            ? SyntaxFactory.Block(elseStatement.WithoutLeadingTrivia())
            : elseStatement;
        var newIf = ifStatement
            .WithCondition(Negate(ifStatement.Condition))
            .WithStatement(body)
            .WithElse(null)
            .WithTrailingTrivia(ifStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);
        return root.ReplaceNode(ifStatement, newIf);
    }

    internal static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        // !!x == x for bool; the caller guarantees the condition is bool.
        if (condition is PrefixUnaryExpressionSyntax { RawKind: (int) SyntaxKind.LogicalNotExpression } not)
        {
            return not.Operand.WithTriviaFrom(condition);
        }

        var operand = condition is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax or ParenthesizedExpressionSyntax or LiteralExpressionSyntax
            or ThisExpressionSyntax
            ? condition.WithoutTrivia()
            : SyntaxFactory.ParenthesizedExpression(condition.WithoutTrivia());
        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operand).WithTriviaFrom(condition);
    }

    private static async Task<bool> IsBooleanAsync(Document document, ExpressionSyntax condition, CancellationToken cancellationToken)
    {
        // Only for plain bool is '!' guaranteed to be the exact negation (user-defined true/false operators are not).
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || model.GetTypeInfo(condition, cancellationToken).Type?.SpecialType != SpecialType.System_Boolean)
        {
            return false;
        }

        return condition is not PrefixUnaryExpressionSyntax { RawKind: (int) SyntaxKind.LogicalNotExpression } not ||
               model.GetTypeInfo(not.Operand, cancellationToken).Type?.SpecialType == SpecialType.System_Boolean;
    }

    private static bool IsEmpty(StatementSyntax statement) =>
        statement is BlockSyntax { Statements.Count: 0 } or EmptyStatementSyntax;

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);
}
