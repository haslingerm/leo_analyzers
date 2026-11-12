using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LeoAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnionSwitchStatementMustBeExhaustiveAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor rule = new(
        Rules.UnionSwitchStatementMustBeExhaustive,
        "Switch over a union type must be exhaustive",
        "Switch statement over union type '{0}' does not handle all possible values",
        Rules.Categories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Keep the package's Roslyn 4.12 baseline. This public property is available
    // only on newer compiler hosts; older hosts simply do not run this rule.
    private static readonly Func<ITypeSymbol, bool>? isUnion = CreateTypeAccessor<Func<ITypeSymbol, bool>>("IsUnion");
    private static readonly Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>? getUnionCaseTypes =
        CreateTypeAccessor<Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>>("UnionCaseTypes");

    private static readonly ImmutableArray<string> exhaustivenessDiagnosticIds =
        ["CS8509", "CS8524", "CS8655", "CS8846", "CS8847"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        if (isUnion is not null)
        {
            context.RegisterCompilationStartAction(start =>
            {
                var original = start.Compilation;
                var compilation = new Lazy<Compilation>(() => CreateProbeCompilation(original));
                start.RegisterSyntaxNodeAction(c => AnalyzeSwitch(c, compilation), SyntaxKind.SwitchStatement);
            });
        }
    }

    private static TDelegate? CreateTypeAccessor<TDelegate>(string propertyName) where TDelegate : Delegate
    {
        var getter = typeof(ITypeSymbol).GetProperty(propertyName)?.GetMethod;
        return getter is null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), getter);
    }

    private static Compilation CreateProbeCompilation(Compilation compilation)
    {
        // LA0003 is independent of the user's settings for the compiler's
        // switch-expression warnings, including #pragma suppression.
        var options = compilation.Options.WithReportSuppressedDiagnostics(true)
            .WithSpecificDiagnosticOptions(compilation.Options.SpecificDiagnosticOptions.SetItems(
                exhaustivenessDiagnosticIds.Select(id =>
                    new System.Collections.Generic.KeyValuePair<string, ReportDiagnostic>(id, ReportDiagnostic.Warn))));
        return compilation.WithOptions(options);
    }

    private static void AnalyzeSwitch(SyntaxNodeAnalysisContext context, Lazy<Compilation> compilation)
    {
        var statement = (SwitchStatementSyntax)context.Node;
        if (statement.ContainsDiagnostics)
        {
            return;
        }

        var typeInfo = context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken);
        var type = typeInfo.Type;
        var unionType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
        if (unionType is null || !isUnion!(unionType))
        {
            return;
        }

        // Prove simple complete coverage using the original semantic model.
        // An inconclusive result always falls back to the compiler below.
        if (UnionSwitchCoverage.IsObviouslyExhaustive(statement, context.SemanticModel,
                unionType, getUnionCaseTypes?.Invoke(unionType) ?? default, typeInfo, context.CancellationToken))
        {
            return;
        }

        var arms = ImmutableArray.CreateBuilder<SwitchExpressionArmSyntax>();
        foreach (var section in statement.Sections)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            foreach (var label in section.Labels)
            {
                PatternSyntax pattern;
                WhenClauseSyntax? guard = null;
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        return;
                    case CasePatternSwitchLabelSyntax patternLabel:
                        pattern = patternLabel.Pattern;
                        guard = patternLabel.WhenClause;
                        break;
                    case CaseSwitchLabelSyntax constantLabel:
                        pattern = SyntaxFactory.ConstantPattern(constantLabel.Value);
                        break;
                    default:
                        return;
                }

                arms.Add(SyntaxFactory.SwitchExpressionArm(pattern, guard,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
            }
        }

        // ISwitchExpressionOperation.IsExhaustive alone does not include the
        // compiler's union-specific and nullable-flow analysis. Ask for the
        // actual compiler diagnostics in a temporary copy of this syntax tree.
        // Only the input, patterns and guards matter; arm bodies return zero.
        var marker = new SyntaxAnnotation();
        var expression = SyntaxFactory.SwitchExpression(SyntaxFactory.ParenthesizedExpression(statement.Expression), SyntaxFactory.SeparatedList(arms))
            .WithAdditionalAnnotations(marker);

        // A cast supplies a result type even for an empty switch. Calling
        // ToString makes a valid statement in void, async and iterator methods,
        // without introducing a local that could shadow a name in the input.
        var replacement = SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParenthesizedExpression(SyntaxFactory.CastExpression(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    SyntaxFactory.ParenthesizedExpression(expression))),
                SyntaxFactory.IdentifierName("ToString")))).WithTriviaFrom(statement);
        var tree = statement.SyntaxTree;
        var root = tree.GetRoot(context.CancellationToken).ReplaceNode(statement, replacement);
        var newTree = tree.WithRootAndOptions(root, tree.Options);
        var newExpression = newTree.GetRoot(context.CancellationToken).GetAnnotatedNodes(marker).Single();
        var probe = compilation.Value.ReplaceSyntaxTree(tree, newTree);

        // This targeted binding is intentional: public operation APIs do not
        // expose the complete union exhaustiveness result. Bind only the member
        // containing the temporary expression, rather than the whole compilation.
#pragma warning disable RS1030
        var diagnostics = probe.GetSemanticModel(newTree).GetDiagnostics(newExpression.Span, context.CancellationToken);
#pragma warning restore RS1030
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
            diagnostics.Any(d => exhaustivenessDiagnosticIds.Contains(d.Id)))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, statement.SwitchKeyword.GetLocation(),
                type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }
}
