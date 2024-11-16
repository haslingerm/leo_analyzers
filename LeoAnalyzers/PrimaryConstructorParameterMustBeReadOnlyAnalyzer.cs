using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LeoAnalyzers;

/*
 * Taken from https://github.com/meziantou/Meziantou.Analyzer/blob/main/src/Meziantou.Analyzer/Rules/PrimaryConstructorParameterShouldBeReadOnlyAnalyzer.cs
 * and modified under MIT License at 2024-11-16, all credit to the original author.
 */

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PrimaryConstructorParameterMustBeReadOnlyAnalyzer : DiagnosticAnalyzer
{
    private const string Message = "Primary constructor parameters must not be reassigned";

    private static readonly DiagnosticDescriptor rule = new(Rules.PrimaryConstructorParameterShouldBeReadOnly,
                                                            Message,
                                                            Message,
                                                            Rules.Categories.Design,
                                                            DiagnosticSeverity.Error,
                                                            true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(compStartContext =>
        {
            compStartContext.RegisterOperationAction(AnalyzerAssignment, OperationKind.SimpleAssignment);
            compStartContext.RegisterOperationAction(AnalyzerAssignment, OperationKind.CompoundAssignment);
            compStartContext.RegisterOperationAction(AnalyzerAssignment, OperationKind.CoalesceAssignment);
            compStartContext.RegisterOperationAction(AnalyzerAssignment, OperationKind.DeconstructionAssignment);
            compStartContext.RegisterOperationAction(AnalyzerIncrementOrDecrement, OperationKind.Increment);
            compStartContext.RegisterOperationAction(AnalyzerIncrementOrDecrement, OperationKind.Decrement);
            compStartContext.RegisterOperationAction(AnalyzerInitializer, OperationKind.VariableDeclarator);
            compStartContext.RegisterOperationAction(AnalyzerArgument, OperationKind.Argument);
        });
    }

    private static void AnalyzerArgument(OperationAnalysisContext context)
    {
        var operation = (IArgumentOperation) context.Operation;
        if (operation.Parameter is { RefKind: RefKind.Ref or RefKind.Out } &&
            IsPrimaryConstructorParameter(operation.Value, context.CancellationToken))
        {
            var diagnostic = Diagnostic.Create(rule,
                                               operation.Value.Syntax.GetLocation(),
                                               operation.Parameter.Name);
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void AnalyzerInitializer(OperationAnalysisContext context)
    {
        var operation = (IVariableDeclaratorOperation) context.Operation;
        if (operation.Initializer is null)
        {
            return;
        }

        if (operation.Symbol.RefKind is RefKind.Ref or RefKind.Out)
        {
            if (IsPrimaryConstructorParameter(operation.Initializer.Value, context.CancellationToken))
            {
                var diagnostic = Diagnostic.Create(rule,
                                                   operation.Initializer.Value.Syntax.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static void AnalyzerIncrementOrDecrement(OperationAnalysisContext context)
    {
        var operation = (IIncrementOrDecrementOperation) context.Operation;
        var target = operation.Target;

        if (IsPrimaryConstructorParameter(target, context.CancellationToken))
        {
            var diagnostic = Diagnostic.Create(rule,
                                               operation.Syntax.GetLocation(),
                                               target.Syntax.ToString());
            context.ReportDiagnostic(diagnostic);
        }
    }

    private void AnalyzerAssignment(OperationAnalysisContext context)
    {
        var operation = (IAssignmentOperation) context.Operation;
        var target = operation.Target;
        if (target is ITupleOperation)
        {
            foreach (var innerTarget in GetAllPrimaryCtorAssignmentTargets(target, context.CancellationToken))
            {
                var parameterName = GetParameterName(innerTarget);
                if (parameterName != null)
                {
                    var diagnostic = Diagnostic.Create(rule,
                                                       innerTarget.Syntax.GetLocation(),
                                                       parameterName);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
        else if (IsPrimaryConstructorParameter(target, context.CancellationToken))
        {
            var parameterName = GetParameterName(target);
            if (parameterName != null)
            {
                var diagnostic = Diagnostic.Create(rule,
                                                   target.Syntax.GetLocation(),
                                                   parameterName);
                context.ReportDiagnostic(diagnostic);
            }
        }

        return;

        static string? GetParameterName(IOperation operation)
        {
            if (operation is IParameterReferenceOperation paramRef)
            {
                return paramRef.Parameter.Name;
            }

            return null;
        }

        static IEnumerable<IOperation> GetAllPrimaryCtorAssignmentTargets(
            IOperation operation, CancellationToken cancellationToken)
        {
            List<IOperation>? result = null;
            GetAllAssignmentTargets(ref result, operation, cancellationToken);

            return result ?? Enumerable.Empty<IOperation>();

            static void GetAllAssignmentTargets(ref List<IOperation>? operations, IOperation operation,
                                                CancellationToken cancellationToken)
            {
                if (operation is ITupleOperation tuple)
                {
                    foreach (var element in tuple.Elements)
                    {
                        GetAllAssignmentTargets(ref operations, element, cancellationToken);
                    }
                }
                else
                {
                    if (IsPrimaryConstructorParameter(operation, cancellationToken))
                    {
                        operations ??= [];
                        operations.Add(operation);
                    }
                }
            }
        }
    }

    private static bool IsPrimaryConstructorParameter(IOperation operation, CancellationToken cancellationToken)
    {
        if (operation is IParameterReferenceOperation
            {
                Parameter.ContainingSymbol: IMethodSymbol
                {
                    MethodKind: MethodKind.Constructor
                } ctor
            })
        {
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax(cancellationToken);
                if (syntax is ClassDeclarationSyntax or StructDeclarationSyntax)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
