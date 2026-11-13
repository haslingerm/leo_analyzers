using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LeoAnalyzers;

/*
 * Taken from https://github.com/meziantou/Meziantou.Analyzer/blob/main/src/Meziantou.Analyzer/Rules/PrimaryConstructorParameterShouldBeReadOnlyAnalyzer.cs
 * under MIT License and modified at 2024-11-16, all credit to the original author.
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

        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment,
            OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment, OperationKind.DeconstructionAssignment);
        context.RegisterOperationAction(AnalyzeIncrementOrDecrement, OperationKind.Increment, OperationKind.Decrement);
        context.RegisterOperationAction(AnalyzeInitializer, OperationKind.VariableDeclarator);
        context.RegisterOperationAction(AnalyzeArgument, OperationKind.Argument);
    }

    private static void AnalyzeArgument(OperationAnalysisContext context)
    {
        var operation = (IArgumentOperation) context.Operation;
        if (operation.Parameter is { RefKind: RefKind.Ref or RefKind.Out })
        {
            ReportWritableReferences(context, operation.Value);
        }
    }

    private static void AnalyzeInitializer(OperationAnalysisContext context)
    {
        // ref int alias = ref parameter; allows writes through the alias.
        var operation = (IVariableDeclaratorOperation) context.Operation;
        if (operation is { Initializer: { } initializer, Symbol.RefKind: RefKind.Ref })
        {
            ReportWritableReferences(context, initializer.Value);
        }
    }

    private static void AnalyzeIncrementOrDecrement(OperationAnalysisContext context)
    {
        var operation = (IIncrementOrDecrementOperation) context.Operation;
        if (IsPrimaryConstructorParameter(operation.Target, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, operation.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var operation = (IAssignmentOperation) context.Operation;

        // alias = ref parameter; rebinds a writable ref to the parameter.
        if (operation is ISimpleAssignmentOperation { IsRef: true } refAssignment && IsWritableRef(refAssignment.Target))
        {
            ReportWritableReferences(context, refAssignment.Value);
        }

        ReportAssignmentTargets(context, operation.Target);
    }

    private static void ReportAssignmentTargets(OperationAnalysisContext context, IOperation target)
    {
        // Deconstruction targets may be nested tuples: (p, (q, r)) = ...
        if (target is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
            {
                ReportAssignmentTargets(context, element);
            }
        }
        else if (IsPrimaryConstructorParameter(target, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, target.Syntax.GetLocation()));
        }
    }

    private static void ReportWritableReferences(OperationAnalysisContext context, IOperation value)
    {
        // A ref conditional can produce a reference to either branch:
        // ref (condition ? ref p : ref q)
        if (value is IConditionalOperation { IsRef: true } conditional)
        {
            ReportWritableReferences(context, conditional.WhenTrue);
            if (conditional.WhenFalse is { } whenFalse)
            {
                ReportWritableReferences(context, whenFalse);
            }
        }
        else if (IsPrimaryConstructorParameter(value, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, value.Syntax.GetLocation()));
        }
    }

    private static bool IsWritableRef(IOperation target) => target switch
    {
        ILocalReferenceOperation local => local.Local.RefKind == RefKind.Ref,
        IParameterReferenceOperation parameter => parameter.Parameter.RefKind is RefKind.Ref or RefKind.Out,
        IFieldReferenceOperation field => field.Field.RefKind == RefKind.Ref,
        _ => false
    };

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
            // A primary constructor is declared by its type declaration,
            // while every other constructor has its own declaration syntax.
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax(cancellationToken) is ClassDeclarationSyntax or StructDeclarationSyntax
                    or RecordDeclarationSyntax)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
