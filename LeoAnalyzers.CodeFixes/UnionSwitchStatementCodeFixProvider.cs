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

    /// <summary>Adds the sections in front of a guard (<c>default: throw ...;</c>) or at the end, keeping only those that compile.</summary>
    private static async Task<Document> AddCompilingSectionsAsync(Document document, SyntaxAnnotation marker,
        IReadOnlyList<SwitchSectionSyntax> newSections, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var switchStatement = Find(root!, marker);
        var sections = switchStatement.Sections;
        int insertAt = sections.Count;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Labels.Any(UnionSwitchCoverage.IsThrowOnlyCatchAll))
            {
                insertAt = i;
                break;
            }
        }

        // Fresh annotation per call, so that only this call's sections are candidates for removal.
        var candidate = new SyntaxAnnotation();
        var annotated = newSections.Select(s => s.WithAdditionalAnnotations(candidate));
        document = document.WithSyntaxRoot(root!.ReplaceNode(switchStatement, switchStatement.WithSections(sections.InsertRange(insertAt, annotated))));

        while (true)
        {
            var currentRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var currentModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var currentSwitch = Find(currentRoot!, marker);
            var errors = currentModel!.GetDiagnostics(currentSwitch.Span, cancellationToken)
                                      .Where(d => d.Severity == DiagnosticSeverity.Error)
                                      .ToList();
            var broken = currentRoot!.GetAnnotatedNodes(candidate)
                                     .Where(s => errors.Any(e => s.Span.Contains(e.Location.SourceSpan)))
                                     .ToList();
            if (broken.Count == 0)
            {
                return document;
            }

            // Removing one section can make a later one valid again only in theory; removing all broken ones
            // terminates because every round removes at least one candidate.
            document = document.WithSyntaxRoot(currentRoot.RemoveNodes(broken, SyntaxRemoveOptions.KeepNoTrivia)!);
        }
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
