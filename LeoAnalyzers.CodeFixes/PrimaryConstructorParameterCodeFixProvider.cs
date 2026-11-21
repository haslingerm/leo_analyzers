using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace LeoAnalyzers.CodeFixes;

/// <summary>
///     Fix for LA0001: introduces a private field initialized from the primary constructor parameter
///     (<c>private int _count = count;</c>) and makes every member use the field instead of the parameter.
///     References in field/property initializers and in the base constructor call keep using the parameter,
///     because they run during construction, where both have the same value.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PrimaryConstructorParameterCodeFixProvider))]
[Shared]
public sealed class PrimaryConstructorParameterCodeFixProvider : CodeFixProvider
{
    internal const string EquivalenceKeyPrefix = Rules.PrimaryConstructorParameterShouldBeReadOnly + ".IntroduceField.";

    public override ImmutableArray<string> FixableDiagnosticIds => [Rules.PrimaryConstructorParameterShouldBeReadOnly];

    // Every fix rewrites all uses of its parameter, which also fixes the parameter's other diagnostics.
    // Batch-merging several such rewrites could insert conflicting fields, so there is no fix-all.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var parameter = node.DescendantNodesAndSelf()
                                .OfType<IdentifierNameSyntax>()
                                .Select(n => model.GetSymbolInfo(n, context.CancellationToken).Symbol)
                                .OfType<IParameterSymbol>()
                                .FirstOrDefault(p => GetDeclaringType(p, context.CancellationToken) is not null);
            if (parameter is null || GetDeclaringType(parameter, context.CancellationToken) is not { } typeDeclaration)
            {
                continue;
            }

            if (IsInInitializer(node))
            {
                // The write happens during construction (initializer or base call): a field would not change that.
                continue;
            }

            var solution = context.Document.Project.Solution;
            var references = await FindReferencesAsync(solution, parameter, context.CancellationToken).ConfigureAwait(false);
            if (references.Any(r => r is { InInitializer: true, InFunction: true }))
            {
                // A lambda in an initializer captures the parameter; it cannot use an instance field instead.
                continue;
            }

            if (IsMentionedInInactiveCode(parameter, context.CancellationToken))
            {
                // Uses in inactive '#if' branches cannot be found, so they would keep using the parameter.
                continue;
            }

            if (await IsWrittenDuringConstructionAsync(solution, references, context.CancellationToken).ConfigureAwait(false))
            {
                // The new field's initializer runs first: members would see the value from before that write.
                continue;
            }

            string fieldName = GetFieldName(parameter, context.CancellationToken);
            context.RegisterCodeFix(
                CodeAction.Create($"Introduce private field '{fieldName}' for '{parameter.Name}'",
                    ct => IntroduceFieldAsync(solution, parameter, typeDeclaration, fieldName, ct),
                    EquivalenceKeyPrefix + parameter.Name),
                diagnostic);
        }
    }

    private static TypeDeclarationSyntax? GetDeclaringType(IParameterSymbol parameter, CancellationToken cancellationToken)
    {
        // Records turn their parameters into properties: members never see the parameter, so there is nothing to fix.
        if (parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            return null;
        }

        foreach (var syntaxReference in constructor.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is (ClassDeclarationSyntax or StructDeclarationSyntax)
                and TypeDeclarationSyntax { ParameterList: not null } typeDeclaration)
            {
                return typeDeclaration;
            }
        }

        return null;
    }

    private static string GetFieldName(IParameterSymbol parameter, CancellationToken cancellationToken)
    {
        // Avoid every name the type already uses: members (also inherited ones) and any identifier in its
        // declarations, so that no local, parameter or lambda parameter can capture the new field's name.
        var taken = new HashSet<string>();
        for (var type = parameter.ContainingType; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                taken.Add(member.Name);
            }
        }

        foreach (var declaration in parameter.ContainingType.DeclaringSyntaxReferences)
        {
            foreach (var token in declaration.GetSyntax(cancellationToken).DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    taken.Add(token.ValueText);
                }
            }
        }

        string baseName = "_" + parameter.Name.TrimStart('_');
        string name = baseName;
        for (int i = 2; taken.Contains(name); i++)
        {
            name = baseName + i;
        }

        return name;
    }

    private static async Task<Solution> IntroduceFieldAsync(Solution solution, IParameterSymbol parameter,
        TypeDeclarationSyntax typeDeclaration, string fieldName, CancellationToken cancellationToken)
    {
        var references = await FindReferencesAsync(solution, parameter, cancellationToken).ConfigureAwait(false);
        var declaringDocumentId = solution.GetDocumentId(typeDeclaration.SyntaxTree)!;
        // 'nameof(count)' keeps naming the parameter: it still exists, and messages/ParamName stay the same.
        var replacedByDocument = references.Where(r => r is { InInitializer: false, InNameof: false })
                                           .GroupBy(r => r.DocumentId)
                                           .ToDictionary(g => g.Key, g => g.Select(r => r.Node).ToList());
        if (!replacedByDocument.ContainsKey(declaringDocumentId))
        {
            replacedByDocument[declaringDocumentId] = [];
        }

        foreach (var entry in replacedByDocument)
        {
            var document = solution.GetDocument(entry.Key)!;
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var nodes = new List<SyntaxNode>(entry.Value);
            if (entry.Key == declaringDocumentId)
            {
                nodes.Add(typeDeclaration);
            }

            // One pass: the type declaration is rebuilt from its already rewritten members.
            var newRoot = root!.ReplaceNodes(nodes, (original, rewritten) => original == typeDeclaration
                ? AddField((TypeDeclarationSyntax) rewritten, typeDeclaration, parameter.Name, fieldName)
                : SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(original));
            solution = solution.WithDocumentSyntaxRoot(entry.Key, newRoot);
        }

        return solution;
    }

    private static TypeDeclarationSyntax AddField(TypeDeclarationSyntax type, TypeDeclarationSyntax original,
        string parameterName, string fieldName)
    {
        var parameterSyntax = original.ParameterList!.Parameters.First(p => p.Identifier.ValueText == parameterName);
        var field = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(parameterSyntax.Type!.WithoutTrivia())
                    .AddVariables(SyntaxFactory.VariableDeclarator(fieldName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.IdentifierName(parameterSyntax.Identifier.WithoutTrivia())))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Separate the new field from following non-field members by a blank line, using the file's line ending.
        var endOfLine = original.SyntaxTree.GetRoot().DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        if (endOfLine.IsKind(SyntaxKind.None))
        {
            endOfLine = SyntaxFactory.ElasticCarriageReturnLineFeed;
        }

        field = type.Members.Count > 0 && type.Members[0] is not BaseFieldDeclarationSyntax
            ? field.WithTrailingTrivia(endOfLine, endOfLine)
            : field.WithTrailingTrivia(endOfLine);
        var newType = type.WithMembers(type.Members.Insert(0, field));
        if (type.OpenBraceToken.IsKind(SyntaxKind.None) || type.OpenBraceToken.IsMissing)
        {
            // 'class C(int x) : Base(ref x);' has no body yet.
            newType = newType.WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                             .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                                                               .WithTrailingTrivia(type.SemicolonToken.TrailingTrivia))
                             .WithSemicolonToken(default);

            // The new body has no layout yet; it only contains the new field.
            return newType.WithAdditionalAnnotations(Formatter.Annotation);
        }

        // Only the new field is formatted; the rest of the type keeps the user's formatting.
        return newType;
    }

    private static async Task<IReadOnlyList<ParameterReference>> FindReferencesAsync(Solution solution, IParameterSymbol parameter,
        CancellationToken cancellationToken)
    {
        var result = new List<ParameterReference>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(parameter, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var location in referenced.Locations)
            {
                var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true) is not IdentifierNameSyntax node)
                {
                    continue;
                }

                bool inInitializer = IsInInitializer(node);
                bool inFunction = node.Ancestors().TakeWhile(a => a is not TypeDeclarationSyntax)
                                      .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
                bool inNameof = false;
                foreach (var invocation in node.Ancestors().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
                    {
                        var model = await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        inNameof = model?.GetOperation(invocation, cancellationToken) is INameOfOperation;
                        break;
                    }
                }

                result.Add(new ParameterReference(location.Document.Id, node, inInitializer, inFunction, inNameof));
            }
        }

        return result;
    }

    private static bool IsMentionedInInactiveCode(IParameterSymbol parameter, CancellationToken cancellationToken)
    {
        var word = new Regex($@"(?<![\p{{L}}\p{{Nd}}_@]){Regex.Escape(parameter.Name)}(?![\p{{L}}\p{{Nd}}_])");
        return parameter.ContainingType.DeclaringSyntaxReferences.Any(declaration =>
            declaration.GetSyntax(cancellationToken).DescendantTrivia()
                       .Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia) && word.IsMatch(t.ToString())));
    }

    /// <summary>
    ///     Whether any reference to the parameter that runs during construction (initializers, base call) writes it
    ///     or passes it as a writable reference. Checked on the references themselves, so it is independent of
    ///     suppressions (<c>#pragma</c>), generated code and anything else that hides LA0001 diagnostics.
    /// </summary>
    private static async Task<bool> IsWrittenDuringConstructionAsync(Solution solution, IReadOnlyList<ParameterReference> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references.Where(r => r.InInitializer))
        {
            var document = solution.GetDocument(reference.DocumentId);
            var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            // Without an operation the use cannot be classified: better no fix than a wrong one.
            if (model?.GetOperation(reference.Node, cancellationToken) is not { } operation || IsWrite(operation))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWrite(IOperation operation)
    {
        var current = operation;
        while (true)
        {
            switch (current.Parent)
            {
                // (p, q) = ...  and  ref (c ? ref p : ref q): the write targets the enclosing operation.
                case ITupleOperation or IConditionalOperation { IsRef: true }:
                    current = current.Parent;
                    break;
                case IAssignmentOperation assignment:
                    return assignment.Target == current;
                case IIncrementOrDecrementOperation:
                    return true;
                case IArgumentOperation argument:
                    return argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out;
                default:
                    return false;
            }
        }
    }

    /// <summary>Whether the node runs during construction: in a field, event or property initializer, or in the base constructor call.</summary>
    private static bool IsInInitializer(SyntaxNode node) =>
        node.AncestorsAndSelf().Any(a =>
            a is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } }
                or EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }
                or PrimaryConstructorBaseTypeSyntax);

    private sealed class ParameterReference(DocumentId documentId, IdentifierNameSyntax node, bool inInitializer,
        bool inFunction, bool inNameof)
    {
        public DocumentId DocumentId { get; } = documentId;
        public IdentifierNameSyntax Node { get; } = node;
        public bool InInitializer { get; } = inInitializer;
        public bool InFunction { get; } = inFunction;
        public bool InNameof { get; } = inNameof;
    }
}
