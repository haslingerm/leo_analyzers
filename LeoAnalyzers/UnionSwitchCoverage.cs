using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LeoAnalyzers;

internal static class UnionSwitchCoverage
{
    // This only proves exhaustiveness. False means "ask the compiler", never
    // "report a missing case". Exact type matches avoid reproducing Roslyn's
    // conversion, inheritance, variance and recursive-pattern rules.
    internal static bool IsObviouslyExhaustive(SwitchStatementSyntax statement, SemanticModel model,
        ITypeSymbol unionType, ImmutableArray<ITypeSymbol> caseTypes, TypeInfo input, CancellationToken cancellationToken)
    {
        var coveredTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var coversNull = false;
        foreach (var section in statement.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var label in section.Labels)
            {
                if (label is DefaultSwitchLabelSyntax)
                {
                    return true;
                }

                if (label is CaseSwitchLabelSyntax constantLabel)
                {
                    // A qualified name or alias can be parsed as a constant
                    // label and subsequently bound by Roslyn as a type pattern.
                    if (model.GetSymbolInfo(constantLabel.Value, cancellationToken).Symbol is ITypeSymbol type)
                    {
                        coveredTypes.Add(type);
                    }
                    else
                    {
                        coversNull |= IsNullConstant(constantLabel.Value, model, cancellationToken);
                    }
                    continue;
                }

                if (label is not CasePatternSwitchLabelSyntax { WhenClause: null } patternLabel)
                {
                    continue;
                }

                var pattern = patternLabel.Pattern;
                while (pattern is ParenthesizedPatternSyntax parenthesized)
                {
                    pattern = parenthesized.Pattern;
                }

                if (pattern is VarPatternSyntax)
                {
                    return true;
                }

                if (pattern is ConstantPatternSyntax constant)
                {
                    coversNull |= IsNullConstant(constant.Expression, model, cancellationToken);
                    continue;
                }

                var typeSyntax = pattern switch
                {
                    DeclarationPatternSyntax declaration => declaration.Type,
                    TypePatternSyntax type => type.Type,
                    _ => null
                };
                if (typeSyntax is not null && model.GetTypeInfo(typeSyntax, cancellationToken).Type is { } matchedType)
                {
                    coveredTypes.Add(matchedType);
                }
            }
        }

        var inputCanBeNull = input.Type is { IsReferenceType: true } or
            INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (!coversNull && (input.Nullability.FlowState == NullableFlowState.MaybeNull ||
            inputCanBeNull && input.Nullability.FlowState != NullableFlowState.NotNull))
        {
            return false;
        }

        if (caseTypes.IsDefaultOrEmpty)
        {
            return false;
        }

        var hasReferenceMember = false;
        foreach (var caseType in caseTypes)
        {
            // Nullable member types and open type parameters need the compiler's
            // full analysis even if a type label appears to match the member.
            if (caseType.TypeKind is TypeKind.Error or TypeKind.TypeParameter ||
                caseType.NullableAnnotation == NullableAnnotation.Annotated ||
                caseType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } ||
                (caseType.IsReferenceType && !HasKnownNonNullableReferenceCase(unionType, caseType)) ||
                !coveredTypes.Contains(caseType))
            {
                return false;
            }
            hasReferenceMember |= caseType.IsReferenceType;
        }

        // Union null-state is richer than TypeInfo.Nullability: a local can
        // hold a null reference member even when the union struct is NotNull.
        // Without an explicit null case, only trust the declared input contract
        // of an unmodified parameter in the current function. The compiler
        // retains responsibility for locals, captures and mutable inputs.
        return coversNull || !hasReferenceMember || HasStableParameterInput(statement, model, cancellationToken);
    }

    private static bool HasStableParameterInput(SwitchStatementSyntax statement, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(statement.Expression, cancellationToken).Symbol is not IParameterSymbol
            { RefKind: RefKind.None } parameter || !parameter.GetAttributes().IsEmpty ||
            !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol,
                model.GetEnclosingSymbol(statement.SpanStart, cancellationToken)))
        {
            return false;
        }

        BlockSyntax? body = null;
        foreach (var ancestor in statement.Ancestors())
        {
            if (ancestor is BaseMethodDeclarationSyntax method)
            {
                // A constructor initializer can write a parameter before the
                // body, outside the region inspected by AnalyzeDataFlow.
                if (method is ConstructorDeclarationSyntax { Initializer: not null })
                {
                    return false;
                }
                body = method.Body;
                break;
            }
            if (ancestor is LocalFunctionStatementSyntax local)
            {
                body = local.Body;
                break;
            }
            if (ancestor is AnonymousFunctionExpressionSyntax lambda)
            {
                body = lambda.Body as BlockSyntax;
                break;
            }
            if (ancestor is AccessorDeclarationSyntax accessor)
            {
                body = accessor.Body;
                break;
            }
        }

        if (body is null)
        {
            return false;
        }

        var flow = model.AnalyzeDataFlow(body);
        if (flow is not { Succeeded: true })
        {
            return false;
        }

        foreach (var written in flow.WrittenInside)
        {
            if (SymbolEqualityComparer.Default.Equals(written, parameter))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasKnownNonNullableReferenceCase(ITypeSymbol unionType, ITypeSymbol caseType)
    {
        // UnionCaseTypes currently drops reference nullability. A native source
        // union's synthesized constructor retains the actual member annotation.
        // User-defined constructors and imported metadata do not provide this
        // guarantee, so leave those cases to the compiler.
        if (unionType is INamedTypeSymbol named)
        {
            foreach (var constructor in named.InstanceConstructors)
            {
                if (constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 1 &&
                    SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, caseType))
                {
                    return constructor.Parameters[0].NullableAnnotation == NullableAnnotation.NotAnnotated;
                }
            }
        }

        return false;
    }

    private static bool IsNullConstant(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        var constant = model.GetConstantValue(expression, cancellationToken);
        return constant.HasValue && constant.Value is null;
    }
}
