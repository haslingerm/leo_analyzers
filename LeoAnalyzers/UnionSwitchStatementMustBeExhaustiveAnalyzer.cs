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

        var constants = new SwitchPatternConstantRewriter(context.SemanticModel, context.CancellationToken);
        var arms = ImmutableArray.CreateBuilder<SwitchExpressionArmSyntax>();
        foreach (var section in statement.Sections)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            foreach (var label in section.Labels)
            {
                PatternSyntax pattern;
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        return;
                    case CasePatternSwitchLabelSyntax patternLabel:
                        // Only a compile-time true guard contributes guaranteed
                        // coverage. Keep all other guards in the original switch,
                        // where their declarations, scope and side effects belong.
                        if (patternLabel.WhenClause is { } when &&
                            context.SemanticModel.GetConstantValue(when.Condition, context.CancellationToken) is not
                                { HasValue: true, Value: true })
                        {
                            continue;
                        }
                        pattern = (PatternSyntax)constants.Visit(patternLabel.Pattern)!;
                        break;
                    case CaseSwitchLabelSyntax constantLabel:
                        pattern = SyntaxFactory.ConstantPattern(constants.ResolveConstant(constantLabel.Value));
                        break;
                    default:
                        return;
                }

                arms.Add(SyntaxFactory.SwitchExpressionArm(pattern, null,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
            }
        }

        var tree = statement.SyntaxTree;
        var root = tree.GetRoot(context.CancellationToken);
        // The probe variable is scoped to its own switch section. It can only
        // clash with names used inside this statement or declared in an
        // enclosing scope, so the scan need not cover the whole file.
        var inputName = "__LA0003_input_" + statement.SpanStart;
        while (statement.DescendantTokens().Any(t => t.ValueText == inputName) ||
            !context.SemanticModel.LookupSymbols(statement.SpanStart, name: inputName).IsEmpty)
        {
            inputName += "_";
        }

        // Keep the original switch intact, including its guards, declarations
        // and loop-carried assignments. A final guarded case captures the same
        // input value without evaluating the governing expression a second time.
        var marker = new SyntaxAnnotation();
        var expression = SyntaxFactory.SwitchExpression(SyntaxFactory.IdentifierName(inputName), SyntaxFactory.SeparatedList(arms))
            .WithAdditionalAnnotations(marker);

        // Equals is not a compile-time constant. Its false path preserves the
        // original no-match path during flow analysis, while its true path lets
        // Roslyn analyze the probe. Nothing in this temporary tree is executed.
        // The cast supplies a result type even when no patterns cover the input.
        var guardCondition = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
            SyntaxFactory.ParseExpression("(0).Equals(1)"),
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                SyntaxFactory.CastExpression(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    SyntaxFactory.ParenthesizedExpression(expression)),
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
        var probeLabel = SyntaxFactory.CasePatternSwitchLabel(
            SyntaxFactory.VarPattern(SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(inputName))),
            SyntaxFactory.WhenClause(guardCondition), SyntaxFactory.Token(SyntaxKind.ColonToken));
        var probeSection = SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(probeLabel),
                SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.BreakStatement()))
            .WithLeadingTrivia(NullableDirectives(context.SemanticModel.GetNullableContext(statement.SwitchKeyword.SpanStart)))
            .WithTrailingTrivia(NullableDirectives(context.SemanticModel.GetNullableContext(statement.CloseBraceToken.FullSpan.Start)));
        root = root.ReplaceNode(statement, statement.AddSections(probeSection));
        var newTree = tree.WithRootAndOptions(root, tree.Options);
        var newExpression = (SwitchExpressionSyntax)newTree.GetRoot(context.CancellationToken).GetAnnotatedNodes(marker).Single();
        var probe = compilation.Value.ReplaceSyntaxTree(tree, newTree);

        // This targeted binding is intentional: public operation APIs do not
        // expose the complete union exhaustiveness result. Bind only the member
        // containing the temporary expression, rather than the whole compilation.
#pragma warning disable RS1030
        var diagnostics = probe.GetSemanticModel(newTree).GetDiagnostics(newExpression.Span, context.CancellationToken);
#pragma warning restore RS1030
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
            diagnostics.Any(d => d.Location.SourceTree == newTree && d.Location.SourceSpan == newExpression.SwitchKeyword.Span &&
                exhaustivenessDiagnosticIds.Contains(d.Id)))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, statement.SwitchKeyword.GetLocation(),
                type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static SyntaxTriviaList NullableDirectives(NullableContext context)
    {
        // Directives inside the original cases must not change the probe's
        // nullable-warning context or leak a new context into following code.
        var warnings = (context & NullableContext.WarningsEnabled) != 0 ? "enable" : "disable";
        var annotations = (context & NullableContext.AnnotationsEnabled) != 0 ? "enable" : "disable";
        return SyntaxFactory.ParseLeadingTrivia("\n#nullable " + warnings + " warnings\n#nullable " + annotations + " annotations\n");
    }
}
