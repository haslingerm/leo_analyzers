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
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace LeoAnalyzers.CodeFixes;

/// <summary>
///     Fix for LA0003: adds a <c>case T: throw new NotImplementedException();</c> section for every case type of the
///     union that the switch statement does not handle yet, and <c>case null:</c> where that is what is missing.
///     Rather than re-implementing the compiler's pattern semantics, the fix lets the compiler and the analyzer decide:
///     <list type="number">
///         <item>a section for every case type is added; sections the existing patterns already handle (CS8120)
///         or that do not compile for any other reason are dropped again,</item>
///         <item>if LA0003 still reports the switch, a <c>case null:</c> section is added and kept only if that
///         resolves the diagnostic.</item>
///     </list>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnionSwitchStatementCodeFixProvider))]
[Shared]
public sealed class UnionSwitchStatementCodeFixProvider : CodeFixProvider
{
    internal const string EquivalenceKey = Rules.UnionSwitchStatementMustBeExhaustive + ".AddMissingCases";

    private static readonly SyntaxAnnotation addedSection = new("LeoAnalyzers.AddedUnionCase");

    public override ImmutableArray<string> FixableDiagnosticIds => [Rules.UnionSwitchStatementMustBeExhaustive];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in context.Diagnostics)
        {
            var switchStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?.AncestorsAndSelf()
                                       .OfType<SwitchStatementSyntax>().FirstOrDefault();
            if (switchStatement is null)
            {
                continue;
            }

            // Only offer the fix if it actually adds something.
            if (await AddMissingCasesAsync(context.Document, switchStatement, context.CancellationToken).ConfigureAwait(false) is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create("Add missing cases",
                    async ct => await AddMissingCasesAsync(context.Document, switchStatement, ct).ConfigureAwait(false) ?? context.Document,
                    EquivalenceKey),
                diagnostic);
        }
    }

    private static async Task<Document?> AddMissingCasesAsync(Document document, SwitchStatementSyntax switchStatement,
        CancellationToken cancellationToken)
    {
        // With '#if' regions between the braces, a new section could end up inside a region and only exist in
        // some build configurations.
        var body = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(switchStatement.OpenBraceToken.SpanStart,
            switchStatement.CloseBraceToken.Span.End);
        if (switchStatement.DescendantTrivia(body).Any(t => t.IsDirective))
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null || UnionSymbols.GetCaseTypes is null ||
            UnionSymbols.AsUnion(model.GetTypeInfo(switchStatement.Expression, cancellationToken).Type) is not { } unionType)
        {
            return null;
        }

        var typeCases = new List<SwitchSectionSyntax>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var caseType in UnionSymbols.GetCaseTypes(unionType))
        {
            // Type patterns cannot use nullable types: 'case int?:' and 'case string?:' are errors.
            var patternType = caseType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableCase
                ? nullableCase.TypeArguments[0]
                : caseType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if (patternType.TypeKind == TypeKind.Error || !seen.Add(patternType))
            {
                continue;
            }

            typeCases.Add(Section(ParseCaseLabel(patternType.ToMinimalDisplayString(model, switchStatement.SpanStart))));
        }

        var marker = new SyntaxAnnotation();
        document = document.WithSyntaxRoot(root.ReplaceNode(switchStatement, switchStatement.WithAdditionalAnnotations(marker)));
        document = await AddCompilingSectionsAsync(document, marker, typeCases, cancellationToken).ConfigureAwait(false);

        if (await IsReportedAsync(document, marker, cancellationToken).ConfigureAwait(false))
        {
            var nullCase = Section(ParseCaseLabel("null"));
            var withNull = await AddCompilingSectionsAsync(document, marker, [nullCase], cancellationToken).ConfigureAwait(false);
            if (!await IsReportedAsync(withNull, marker, cancellationToken).ConfigureAwait(false))
            {
                document = withNull;
            }
        }

        var finalRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (!finalRoot!.GetAnnotatedNodes(addedSection).Any())
        {
            return null;
        }

        document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds the sections one by one after the last section that is not a guard (<c>default: throw ...;</c>), so a
    ///     trailing guard stays last and no new section precedes an existing case. A section is only kept if the
    ///     document does not get any additional error, wherever the compiler reports it: an already handled case
    ///     can be reported on the existing label rather than on the new one.
    /// </summary>
    private static async Task<Document> AddCompilingSectionsAsync(Document document, SyntaxAnnotation marker,
        IReadOnlyList<SwitchSectionSyntax> newSections, CancellationToken cancellationToken)
    {
        int errors = await CountErrorsAsync(document, cancellationToken).ConfigureAwait(false);
        foreach (var section in newSections)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var switchStatement = Find(root!, marker);
            var sections = switchStatement.Sections;
            int insertAt = 0;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                if (!sections[i].Labels.Any(UnionSwitchCoverage.IsThrowOnlyCatchAll))
                {
                    insertAt = i + 1;
                    break;
                }
            }

            var candidate = document.WithSyntaxRoot(root!.ReplaceNode(switchStatement,
                switchStatement.WithSections(sections.Insert(insertAt, section))));
            int candidateErrors = await CountErrorsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (candidateErrors <= errors)
            {
                document = candidate;
                errors = candidateErrors;
            }
        }

        return document;
    }

    private static async Task<int> CountErrorsAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return model!.GetDiagnostics(cancellationToken: cancellationToken).Count(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static async Task<bool> IsReportedAsync(Document document, SyntaxAnnotation marker, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var switchStatement = Find(root!, marker);
        var analyzers = model!.Compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new UnionSwitchStatementMustBeExhaustiveAnalyzer()),
            new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty), null, concurrentAnalysis: false,
                logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
        var diagnostics = await analyzers.GetAnalyzerSemanticDiagnosticsAsync(model, switchStatement.Span, cancellationToken).ConfigureAwait(false);
        return diagnostics.Any(d => d.Id == Rules.UnionSwitchStatementMustBeExhaustive &&
                                    d.Location.SourceSpan == switchStatement.SwitchKeyword.Span);
    }

    private static SwitchStatementSyntax Find(SyntaxNode root, SyntaxAnnotation marker) =>
        root.GetAnnotatedNodes(marker).OfType<SwitchStatementSyntax>().Single();

    /// <summary>
    ///     Parses <c>case {text}:</c>, so the label has exactly the shape the parser would give it
    ///     (<c>case int:</c> is a type pattern, <c>case NotFound:</c> a constant-style label).
    /// </summary>
    private static SwitchLabelSyntax ParseCaseLabel(string text)
    {
        var statement = (SwitchStatementSyntax) SyntaxFactory.ParseStatement($"switch (x) {{ case {text}: break; }}");
        return statement.Sections[0].Labels[0].WithoutTrivia();
    }

    private static SwitchSectionSyntax Section(SwitchLabelSyntax label)
    {
        var exception = SyntaxFactory.ParseTypeName("global::System.NotImplementedException")
                                     .WithAdditionalAnnotations(Simplifier.Annotation);
        var body = SyntaxFactory.ThrowStatement(
            SyntaxFactory.ObjectCreationExpression(exception).WithArgumentList(SyntaxFactory.ArgumentList()));
        return SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList(label), SyntaxFactory.SingletonList<StatementSyntax>(body))
                            .WithAdditionalAnnotations(addedSection, Formatter.Annotation);
    }
}
