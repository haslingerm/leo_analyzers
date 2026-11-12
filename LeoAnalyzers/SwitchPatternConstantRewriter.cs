using System;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LeoAnalyzers;

internal sealed class SwitchPatternConstantRewriter(SemanticModel model, CancellationToken cancellationToken) : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitConstantPattern(ConstantPatternSyntax node) =>
        node.WithExpression(ResolveConstant(node.Expression));

    public override SyntaxNode? VisitRelationalPattern(RelationalPatternSyntax node) =>
        node.WithExpression(ResolveConstant(node.Expression));

    internal ExpressionSyntax ResolveConstant(ExpressionSyntax expression)
    {
        // Resolve before relocation: nameof and constant expressions may refer
        // to variables shared by statement labels but absent from separate arms.
        var constant = model.GetConstantValue(expression, cancellationToken);
        if (!constant.HasValue)
        {
            return expression;
        }

        var literal = constant.Value is null
            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
            : Literal(constant.Value);
        var type = model.GetTypeInfo(expression, cancellationToken).Type;
        if (literal is null || type?.TypeKind == TypeKind.Error)
        {
            return expression;
        }
        if (type is null)
        {
            return constant.Value is null ? literal : expression;
        }

        // Literals alone can widen byte/short constants or lose an enum type.
        // A typed cast retains the original pattern's conversions and domain.
        var typeName = type.ToMinimalDisplayString(model, expression.SpanStart, SymbolDisplayFormat.FullyQualifiedFormat);
        return SyntaxFactory.CastExpression(SyntaxFactory.ParseTypeName(typeName), SyntaxFactory.ParenthesizedExpression(literal));
    }

    private static ExpressionSyntax? Literal(object value) => value switch
    {
        bool boolean => SyntaxFactory.LiteralExpression(boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
        string text => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text)),
        char character => SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(character)),
        sbyte or byte or short or ushort => Number(SyntaxFactory.Literal(Convert.ToInt32(value))),
        int number => Number(SyntaxFactory.Literal(number)),
        uint number => Number(SyntaxFactory.Literal(number)),
        long number => Number(SyntaxFactory.Literal(number)),
        ulong number => Number(SyntaxFactory.Literal(number)),
        float number when float.IsNaN(number) => SyntaxFactory.ParseExpression("float.NaN"),
        float number when float.IsPositiveInfinity(number) => SyntaxFactory.ParseExpression("float.PositiveInfinity"),
        float number when float.IsNegativeInfinity(number) => SyntaxFactory.ParseExpression("float.NegativeInfinity"),
        float number => Number(SyntaxFactory.Literal(number)),
        double number when double.IsNaN(number) => SyntaxFactory.ParseExpression("double.NaN"),
        double number when double.IsPositiveInfinity(number) => SyntaxFactory.ParseExpression("double.PositiveInfinity"),
        double number when double.IsNegativeInfinity(number) => SyntaxFactory.ParseExpression("double.NegativeInfinity"),
        double number => Number(SyntaxFactory.Literal(number)),
        decimal number => Number(SyntaxFactory.Literal(number)),
        _ => null
    };

    private static LiteralExpressionSyntax Number(SyntaxToken token) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
}
