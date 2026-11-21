using System.Collections.Generic;
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

            if (statement.Parent is ElseClauseSyntax { Parent: IfStatementSyntax ifWithEmptyElse } elseClause)
            {
                // The whole clause is removed: it must not carry comments (an explanation, or a comment that would
                // swallow following code if moved) or directives (removing '#if'/'#else' orphans their '#endif').
                if (HasCommentsOrDirectives(elseClause.DescendantTrivia()))
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create("Remove empty else",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(RemoveElse(root, ifWithEmptyElse))),
                        RemoveElseKey),
                    diagnostic);
            }
            else if (statement.Parent is IfStatementSyntax ifStatement && ifStatement.Statement == statement &&
                     ifStatement.Else is { Statement: var elseStatement } invertedElse && !IsEmpty(elseStatement) &&
                     !HasCommentsOrDirectives(RemovedByInvert(ifStatement, invertedElse)) &&
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
        var newIf = ifStatement.WithElse(null);

        // The then-branch's trailing trivia (e.g. '} // explanation') is kept. Only when it does not end the line,
        // the line continues as after the else clause (e.g. 'if (b) Work(); else { } Next();').
        var thenTrailing = newIf.GetTrailingTrivia();
        if (!thenTrailing.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            newIf = newIf.WithTrailingTrivia(HasCommentsOrDirectives(thenTrailing)
                ? thenTrailing.AddRange(elseClause.GetTrailingTrivia())
                : elseClause.GetTrailingTrivia());
        }

        return root.ReplaceNode(ifStatement, KeepEnclosingElseBinding(ifStatement, newIf));
    }

    /// <summary>The trivia that inverting drops or moves: around the empty body and the 'else' keyword.</summary>
    private static IEnumerable<SyntaxTrivia> RemovedByInvert(IfStatementSyntax ifStatement, ElseClauseSyntax elseClause)
    {
        var removed = ifStatement.CloseParenToken.TrailingTrivia
                                 .Concat(ifStatement.Statement.DescendantTrivia())
                                 .Concat(elseClause.ElseKeyword.LeadingTrivia)
                                 .Concat(elseClause.ElseKeyword.TrailingTrivia);
        return elseClause.Statement is IfStatementSyntax elseIf ? removed.Concat(elseIf.GetLeadingTrivia()) : removed;
    }

    private static bool HasCommentsOrDirectives(IEnumerable<SyntaxTrivia> trivia) =>
        trivia.Any(t => t.IsDirective || IsComment(t) || t.IsKind(SyntaxKind.DisabledTextTrivia));

    /// <summary>
    ///     The fixes turn an if-else into an if without else. If that if ends the then-branch of an enclosing if
    ///     that has an else (possibly through loops, labels or else-if chains), the enclosing else would now bind
    ///     to it instead: <c>if (a) if (b) X(); else { } else Y();</c>. In that case the result is wrapped in a block.
    /// </summary>
    private static StatementSyntax KeepEnclosingElseBinding(IfStatementSyntax original, IfStatementSyntax replacement)
    {
        if (!WouldCaptureEnclosingElse(original))
        {
            // The user's code keeps its formatting; only newly built syntax is formatted.
            return replacement;
        }

        // The wrapped if moves one level deeper, so the new block is formatted (including its re-indented content).
        return SyntaxFactory.Block(replacement.WithoutLeadingTrivia().WithoutTrailingTrivia())
                            .WithLeadingTrivia(replacement.GetLeadingTrivia())
                            .WithTrailingTrivia(replacement.GetTrailingTrivia())
                            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    internal static bool WouldCaptureEnclosingElse(IfStatementSyntax ifStatement)
    {
        // Walk up through every construct whose embedded statement ends it: an else-less if at its end stays "open".
        SyntaxNode current = ifStatement;
        while (true)
        {
            switch (current.Parent)
            {
                case IfStatementSyntax enclosing when enclosing.Statement == current:
                    if (enclosing.Else is not null)
                    {
                        return true;
                    }

                    current = enclosing;
                    break;
                case ElseClauseSyntax { Parent: IfStatementSyntax enclosing }:
                    current = enclosing;
                    break;
                case WhileStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or LockStatementSyntax
                    or UsingStatementSyntax or FixedStatementSyntax or LabeledStatementSyntax:
                    current = current.Parent;
                    break;
                default:
                    return false;
            }
        }
    }

    private static SyntaxNode Invert(SyntaxNode root, IfStatementSyntax ifStatement, StatementSyntax elseStatement)
    {
        // An else-if becomes the body of the inverted if; a block keeps a dangling else from changing its meaning.
        var body = elseStatement is IfStatementSyntax
            ? SyntaxFactory.Block(elseStatement.WithoutLeadingTrivia()).WithAdditionalAnnotations(Formatter.Annotation)
            : elseStatement;
        var newIf = ifStatement
            .WithCondition(Negate(ifStatement.Condition))
            .WithStatement(body)
            .WithElse(null)
            .WithTrailingTrivia(ifStatement.GetTrailingTrivia());
        if (elseStatement is not IfStatementSyntax)
        {
            // Keep the else body's layout: the trivia after 'else' (line break or space) now follows ')'.
            newIf = newIf.WithCloseParenToken(newIf.CloseParenToken.WithTrailingTrivia(ifStatement.Else!.ElseKeyword.TrailingTrivia));
        }

        return root.ReplaceNode(ifStatement, KeepEnclosingElseBinding(ifStatement, newIf));
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
