using System.Threading.Tasks;
using Xunit;
using Verify = LeoAnalyzers.CodeFixes.Tests.Verifier<LeoAnalyzers.PrimaryConstructorParameterMustBeReadOnlyAnalyzer,
    LeoAnalyzers.CodeFixes.PrimaryConstructorParameterCodeFixProvider>;

namespace LeoAnalyzers.CodeFixes.Tests;

public sealed class PrimaryConstructorParameterCodeFixTests
{
    [Fact]
    public async ValueTask ParameterUsedInInactiveCode_HasNoFix()
    {
        // References in inactive '#if' branches are invisible to the rewrite and would keep using the parameter.
        await Verify.VerifyNoFixAsync("""
            class C(int value)
            {
                public void Set() => [|value|] = 1;
            #if UNDEFINED_SYMBOL
                public int Get() => value;
            #endif
            }
            """);
    }

    [Fact]
    public async ValueTask OtherInactiveCode_DoesNotBlockTheFix()
    {
        await Verify.VerifyAsync("""
            class C(int value)
            {
                public void Set() => [|value|] = 1;
            #if UNDEFINED_SYMBOL
                public int Other() => 42;
            #endif
            }
            """, """
            class C(int value)
            {
                private int _value = value;

                public void Set() => _value = 1;
            #if UNDEFINED_SYMBOL
                public int Other() => 42;
            #endif
            }
            """);
    }

    // PR #3 review: the new field's initializer runs first, so a write to the parameter during construction
    // (anywhere, not only at the selected diagnostic) would make members see a stale value.
    [Fact]
    public async ValueTask WriteInAnotherInitializer_MemberWriteHasNoFix()
    {
        await Verify.VerifyNoFixAsync("""
            class C(int value)
            {
                private readonly int _initial = [|value|] = 1;

                public void Set() => [|value|] = 2;
                public int Get() => value;
            }
            """);
    }

    [Fact]
    public async ValueTask RefArgumentInBaseCall_MemberWriteHasNoFix()
    {
        await Verify.VerifyNoFixAsync("""
            class Base
            {
                public Base(ref int x) => x = 1;
            }

            class C(int value) : Base(ref [|value|])
            {
                public void Set() => [|value|] = 2;
                public int Get() => value;
            }
            """);
    }

    [Fact]
    public async ValueTask WriteDuringConstructionInOtherPart_MemberWriteHasNoFix()
    {
        var test = Verify.Create("", "");
        test.TestState.Sources.Clear();
        test.TestState.Sources.Add(("A.cs", """
            partial class C(int value)
            {
                public void Set() => [|value|] = 2;
            }
            """));
        test.TestState.Sources.Add(("B.cs", """
            partial class C
            {
                private readonly int _next = [|value++|];
            }
            """));
        test.FixedState.Sources.Clear();
        test.FixedState.Sources.Add(test.TestState.Sources[0]);
        test.FixedState.Sources.Add(test.TestState.Sources[1]);
        test.FixedState.MarkupHandling = Microsoft.CodeAnalysis.Testing.MarkupMode.Allow;
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask ReadsDuringConstruction_DoNotBlockTheFix()
    {
        await Verify.VerifyAsync("""
            class C(int value)
            {
                private readonly int _initial = value + 1;

                public void Set() => [|value|] = 2;
            }
            """, """
            class C(int value)
            {
                private int _value = value;
                private readonly int _initial = value + 1;

                public void Set() => _value = 2;
            }
            """);
    }

    [Fact]
    public async ValueTask Increment_IntroducesFieldAndRedirectsAllMemberUses()
    {
        await Verify.VerifyAsync("""
            class Counter(int count)
            {
                public void Increment() => [|count++|];
                public int Current => count;
            }
            """, """
            class Counter(int count)
            {
                private int _count = count;

                public void Increment() => _count++;
                public int Current => _count;
            }
            """);
    }

    [Fact]
    public async ValueTask Assignment_InitializersAndBaseCallKeepTheParameter()
    {
        await Verify.VerifyAsync("""
            class Base(int value)
            {
                public int Value { get; } = value;
            }

            class Derived(int value) : Base(value)
            {
                private readonly int _initial = value;
                public int Twice { get; } = value * 2;

                public void Reset() { [|value|] = _initial; }
                public int Get() => value;
            }
            """, """
            class Base(int value)
            {
                public int Value { get; } = value;
            }

            class Derived(int value) : Base(value)
            {
                private int _value = value;
                private readonly int _initial = value;
                public int Twice { get; } = value * 2;

                public void Reset() { _value = _initial; }
                public int Get() => _value;
            }
            """);
    }

    [Fact]
    public async ValueTask CompoundAndDeconstructionWrites_EachParameterGetsItsField()
    {
        var test = Verify.Create("""
            class C(int a, int b)
            {
                public void M()
                {
                    [|a|] += 1;
                    ([|a|], [|b|]) = (b, a);
                }
            }
            """, """
            class C(int a, int b)
            {
                private int _b = b;
                private int _a = a;

                public void M()
                {
                    _a += 1;
                    (_a, _b) = (_b, _a);
                }
            }
            """);
        test.NumberOfIncrementalIterations = 2;
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask RefArgument_IsRedirected()
    {
        await Verify.VerifyAsync("""
            class C(int value)
            {
                public void M() => Bump(ref [|value|]);
                private static void Bump(ref int x) => x++;
            }
            """, """
            class C(int value)
            {
                private int _value = value;

                public void M() => Bump(ref _value);
                private static void Bump(ref int x) => x++;
            }
            """);
    }

    [Fact]
    public async ValueTask NameClashes_WithMembersAndLocals_AreAvoided()
    {
        await Verify.VerifyAsync("""
            class C(int count)
            {
                private int _count;

                public int M()
                {
                    int _count2 = 1;
                    [|count|] = _count + _count2;
                    return count;
                }
            }
            """, """
            class C(int count)
            {
                private int _count3 = count;
                private int _count;

                public int M()
                {
                    int _count2 = 1;
                    _count3 = _count + _count2;
                    return _count3;
                }
            }
            """);
    }

    [Fact]
    public async ValueTask Struct()
    {
        await Verify.VerifyAsync("""
            struct S(int value)
            {
                public void Set(int v) => [|value|] = v;
                public readonly int Get() => value;
            }
            """, """
            struct S(int value)
            {
                private int _value = value;

                public void Set(int v) => _value = v;
                public readonly int Get() => _value;
            }
            """);
    }

    [Fact]
    public async ValueTask LambdaInMember_IsRedirected()
    {
        await Verify.VerifyAsync("""
            using System;

            class C(int value)
            {
                public Action Increment() => () => [|value++|];
                public Func<int> Read() => () => value;
            }
            """, """
            using System;

            class C(int value)
            {
                private int _value = value;

                public Action Increment() => () => _value++;
                public Func<int> Read() => () => _value;
            }
            """);
    }

    [Fact]
    public async ValueTask PartialClass_UsesInAllPartsAreRedirected()
    {
        var test = Verify.Create("", "");
        test.TestState.Sources.Clear();
        test.FixedState.Sources.Clear();
        test.TestState.Sources.Add(("A.cs", """
            partial class C(int value)
            {
                public void Set() => [|value|] = 1;
            }
            """));
        test.TestState.Sources.Add(("B.cs", """
            partial class C
            {
                public int Get() => value;
            }
            """));
        test.FixedState.Sources.Add(("A.cs", """
            partial class C(int value)
            {
                private int _value = value;

                public void Set() => _value = 1;
            }
            """));
        test.FixedState.Sources.Add(("B.cs", """
            partial class C
            {
                public int Get() => _value;
            }
            """));
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask LambdaInInitializerCapturingTheParameter_HasNoFix()
    {
        // The lambda cannot use an instance field, and redirecting only part of the uses would change behavior.
        await Verify.VerifyNoFixAsync("""
            using System;

            class C(int value)
            {
                private readonly Func<int> _read = () => value;

                public void Set() => [|value|] = 1;
            }
            """);
    }

    [Fact]
    public async ValueTask WriteDuringConstruction_HasNoFix()
    {
        await Verify.VerifyNoFixAsync("""
            class C(int value)
            {
                private readonly int _next = [|value++|];
            }
            """);
    }

    [Fact]
    public async ValueTask Record_HasNoFix()
    {
        await Verify.VerifyNoFixAsync("""
            record R(int Value)
            {
                private readonly int _next = [|Value++|];
            }
            """);
    }
}
