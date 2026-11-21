using System.Threading.Tasks;
using Xunit;
using Verify = LeoAnalyzers.CodeFixes.Tests.Verifier<LeoAnalyzers.UnionSwitchStatementMustBeExhaustiveAnalyzer,
    LeoAnalyzers.CodeFixes.UnionSwitchStatementCodeFixProvider>;

namespace LeoAnalyzers.CodeFixes.Tests;

public sealed class UnionSwitchStatementCodeFixTests
{
    // Second review: case types whose short label does not compile fall back to a type-only pattern.
    [Fact]
    public async ValueTask CaseTypeShadowedByMember_UsesTypeOnlyPattern()
    {
        await Verify.VerifyAsync("""
            using System;

            public sealed class Circle;
            public sealed class Square;
            public union Shape(Circle, Square);

            class C
            {
                public Circle Circle { get; } = new();

                void M(Shape shape)
                {
                    [|switch|] (shape)
                    {
                        case Square:
                            break;
                    }
                }
            }
            """, """
            using System;

            public sealed class Circle;
            public sealed class Square;
            public union Shape(Circle, Square);

            class C
            {
                public Circle Circle { get; } = new();

                void M(Shape shape)
                {
                    switch (shape)
                    {
                        case Square:
                            break;
                        case Circle _:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask ConstantNamedLikeCaseType_IsNotMistakenForTheType()
    {
        // 'case Square:' would compile as the constant pattern 1 (an int case); the type has to be qualified.
        await Verify.VerifyAsync("""
            using System;

            public sealed class Square;
            public union Shape(int, Square);

            class C
            {
                const int Square = 1;

                void M(Shape shape)
                {
                    [|switch|] (shape)
                    {
                        case int:
                            break;
                    }
                }
            }
            """, """
            using System;

            public sealed class Square;
            public union Shape(int, Square);

            class C
            {
                const int Square = 1;

                void M(Shape shape)
                {
                    switch (shape)
                    {
                        case int:
                            break;
                        case global::Square:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask TupleCaseType_UsesTypeOnlyPattern()
    {
        await Verify.VerifyAsync("""
            using System;

            public readonly record struct NotFound;
            public union Result((int, string), NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case NotFound:
                            break;
                    }
                }
            }
            """, """
            using System;

            public readonly record struct NotFound;
            public union Result((int, string), NotFound);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case NotFound:
                            break;
                        case ValueTuple<int, string> _:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask SwitchWithConditionalCompilation_HasNoFix()
    {
        // New sections could land inside an '#if' region and only exist in some build configurations.
        await Verify.VerifyNoFixAsync("""
            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int:
                            break;
            #if !UNDEFINED_SYMBOL
                        case string:
                            break;
            #endif
                    }
                }
            }
            """);
    }

    // PR #3 review: with the guard in front of the cases, inserting before it would duplicate existing labels.
    [Fact]
    public async ValueTask GuardBeforeCases_NewCasesGoAfterTheExistingOnes()
    {
        await Verify.VerifyAsync("""
            using System;
            using System.Diagnostics;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        default:
                            throw new UnreachableException();
                        case int:
                            break;
                    }
                }
            }
            """, """
            using System;
            using System.Diagnostics;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        default:
                            throw new UnreachableException();
                        case int:
                            break;
                        case string:
                            throw new NotImplementedException();
                        case NotFound:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask GuardBetweenCases_NewCasesGoAfterTheLastCase()
    {
        await Verify.VerifyAsync("""
            using System;
            using System.Diagnostics;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case string:
                            break;
                        default:
                            throw new UnreachableException();
                        case int:
                            break;
                    }
                }
            }
            """, """
            using System;
            using System.Diagnostics;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case string:
                            break;
                        default:
                            throw new UnreachableException();
                        case int:
                            break;
                        case NotFound:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask MissingCase_IsAdded()
    {
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int:
                            break;
                        case string:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask AllCasesMissing_AreAddedInDeclarationOrder_FullyQualifiedWithoutUsing()
    {
        await Verify.VerifyAsync("""
            public union Result(int, string, Result.Missing)
            {
                public readonly record struct Missing;
            }

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                    }
                }
            }
            """, """
            public union Result(int, string, Result.Missing)
            {
                public readonly record struct Missing;
            }

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int:
                            throw new System.NotImplementedException();
                        case string:
                            throw new System.NotImplementedException();
                        case Result.Missing:
                            throw new System.NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask MissingCases_AreInsertedBeforeGuardDefault()
    {
        await Verify.VerifyAsync("""
            using System;
            using System.Diagnostics;

            public union Result(int, string);

            class C
            {
                int M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int i:
                            return i;
                        default:
                            throw new UnreachableException();
                    }
                }
            }
            """, """
            using System;
            using System.Diagnostics;

            public union Result(int, string);

            class C
            {
                int M(Result result)
                {
                    switch (result)
                    {
                        case int i:
                            return i;
                        case string:
                            throw new NotImplementedException();
                        default:
                            throw new UnreachableException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask CasesAlreadyHandledByComplexPatterns_AreNotDuplicated()
    {
        await Verify.VerifyAsync("""
            using System;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int or string:
                            break;
                    }
                }
            }
            """, """
            using System;

            public readonly record struct NotFound;
            public union Result(int, string, NotFound);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int or string:
                            break;
                        case NotFound:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask GuardedPattern_GetsAnUnguardedCase()
    {
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int i when i > 0:
                            break;
                        case string:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int i when i > 0:
                            break;
                        case string:
                            break;
                        case int:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask NullableUnionInput_GetsNullCase()
    {
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result? result)
                {
                    [|switch|] (result)
                    {
                        case int:
                            break;
                        case string:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result? result)
                {
                    switch (result)
                    {
                        case int:
                            break;
                        case string:
                            break;
                        case null:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask NullIsNotAddedWhenTheCasesAreEnough()
    {
        // string is a reference type, but a non-nullable union input does not need 'case null'.
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case string:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case string:
                            break;
                        case int:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask ObjectTypePattern_OnlyNullIsMissing()
    {
        // Mirrors the compiler: a bare 'object' type pattern does not cover the union's null value.
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case int:
                            break;
                        case object:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int:
                            break;
                        case object:
                            break;
                        case null:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask NullableCaseTypes_UseTheirUnderlyingTypeInPatterns()
    {
        await Verify.VerifyAsync("""
            using System;

            public union Result(int?, string?);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                    }
                }
            }
            """, """
            using System;

            public union Result(int?, string?);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case int:
                            throw new NotImplementedException();
                        case string:
                            throw new NotImplementedException();
                        case null:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask CustomUnionStruct_LikeValueUnion()
    {
        const string CustomUnion = """
            [System.Runtime.CompilerServices.Union]
            public readonly struct Either<T1, T2> : System.Runtime.CompilerServices.IUnion
            {
                public Either(T1 value) => Value = value;
                public Either(T2 value) => Value = value;
                public object? Value { get; }
            }

            """;
        await Verify.VerifyAsync(CustomUnion + """
            class C
            {
                void M(Either<int, string> either)
                {
                    [|switch|] (either)
                    {
                        case int:
                            break;
                    }
                }
            }
            """, CustomUnion + """
            class C
            {
                void M(Either<int, string> either)
                {
                    switch (either)
                    {
                        case int:
                            break;
                        case string:
                            throw new System.NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask CaseTypesFromOtherNamespaces_UseTheShortestValidName()
    {
        await Verify.VerifyAsync("""
            using System;
            using Domain;

            namespace Domain
            {
                public sealed class Rocket;
                public readonly record struct NotFound;
            }

            public union Result(Rocket, NotFound);

            class C
            {
                void M(Result result)
                {
                    [|switch|] (result)
                    {
                        case Rocket:
                            break;
                    }
                }
            }
            """, """
            using System;
            using Domain;

            namespace Domain
            {
                public sealed class Rocket;
                public readonly record struct NotFound;
            }

            public union Result(Rocket, NotFound);

            class C
            {
                void M(Result result)
                {
                    switch (result)
                    {
                        case Rocket:
                            break;
                        case NotFound:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }

    [Fact]
    public async ValueTask FixAll_FixesEverySwitch()
    {
        await Verify.VerifyAsync("""
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result a, Result b)
                {
                    [|switch|] (a)
                    {
                        case int:
                            break;
                    }

                    [|switch|] (b)
                    {
                        case string:
                            break;
                    }
                }
            }
            """, """
            using System;

            public union Result(int, string);

            class C
            {
                void M(Result a, Result b)
                {
                    switch (a)
                    {
                        case int:
                            break;
                        case string:
                            throw new NotImplementedException();
                    }

                    switch (b)
                    {
                        case string:
                            break;
                        case int:
                            throw new NotImplementedException();
                    }
                }
            }
            """);
    }
}
