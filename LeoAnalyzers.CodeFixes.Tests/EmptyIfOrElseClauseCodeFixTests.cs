using System.Threading.Tasks;
using Xunit;
using Verify = LeoAnalyzers.CodeFixes.Tests.Verifier<LeoAnalyzers.EmptyIfOrElseClauseRequiresCommentAnalyzer,
    LeoAnalyzers.CodeFixes.EmptyIfOrElseClauseCodeFixProvider>;

namespace LeoAnalyzers.CodeFixes.Tests;

public sealed class EmptyIfOrElseClauseCodeFixTests
{
    private const string RemoveElse = EmptyIfOrElseClauseCodeFixProvider.RemoveElseKey;
    private const string InvertIf = EmptyIfOrElseClauseCodeFixProvider.InvertIfKey;

    [Fact]
    public async ValueTask EmptyElseBlock_IsRemoved()
    {
        await Verify.VerifyAsync("""
            class C
            {
                void M(bool b)
                {
                    if (b)
                    {
                        Work();
                    }
                    else [|{ }|]
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(bool b)
                {
                    if (b)
                    {
                        Work();
                    }
                }

                void Work() { }
            }
            """, RemoveElse);
    }

    [Fact]
    public async ValueTask EmptyElseStatement_IsRemoved()
    {
        await Verify.VerifyAsync("""
            class C
            {
                void M(bool b)
                {
                    if (b)
                        Work();
                    else [|;|]
                    Work();
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(bool b)
                {
                    if (b)
                        Work();
                    Work();
                }

                void Work() { }
            }
            """, RemoveElse);
    }

    [Fact]
    public async ValueTask EmptyElseOfElseIf_IsRemoved()
    {
        await Verify.VerifyAsync("""
            class C
            {
                void M(int x)
                {
                    if (x > 0)
                    {
                        Work();
                    }
                    else if (x < 0)
                    {
                        Work();
                    }
                    else [|{ }|]
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(int x)
                {
                    if (x > 0)
                    {
                        Work();
                    }
                    else if (x < 0)
                    {
                        Work();
                    }
                }

                void Work() { }
            }
            """, RemoveElse);
    }

    [Theory]
    [InlineData("b", "!b")]
    [InlineData("!b", "b")]
    [InlineData("a && b", "!(a && b)")]
    [InlineData("a || b", "!(a || b)")]
    [InlineData("x > 0.5", "!(x > 0.5)")]
    [InlineData("x == 1", "!(x == 1)")]
    [InlineData("o is string", "!(o is string)")]
    [InlineData("IsReady()", "!IsReady()")]
    [InlineData("Flags[0]", "!Flags[0]")]
    [InlineData("(a)", "!(a)")]
    [InlineData("a ? b : !b", "!(a ? b : !b)")]
    public async ValueTask EmptyIf_IsInverted(string condition, string inverted)
    {
        // Comparisons are negated as a whole, never flipped: '!(x > 0.5)' and 'x <= 0.5' differ for NaN.
        await Verify.VerifyAsync($$"""
            class C
            {
                bool[] Flags = [true];

                void M(bool a, bool b, double x, object o)
                {
                    if ({{condition}}) [|{ }|]
                    else
                    {
                        Work();
                    }
                }

                bool IsReady() => true;
                void Work() { }
            }
            """, $$"""
            class C
            {
                bool[] Flags = [true];

                void M(bool a, bool b, double x, object o)
                {
                    if ({{inverted}})
                    {
                        Work();
                    }
                }

                bool IsReady() => true;
                void Work() { }
            }
            """, InvertIf);
    }

    [Fact]
    public async ValueTask EmptyIfWithElseIf_ElseIfIsWrappedInBlock()
    {
        // Without the block, a following 'else' could bind to the wrong if.
        await Verify.VerifyAsync("""
            class C
            {
                void M(bool a, bool b)
                {
                    if (a) [|{ }|]
                    else if (b)
                    {
                        Work();
                    }
                    else
                    {
                        Work();
                    }
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(bool a, bool b)
                {
                    if (!a)
                    {
                        if (b)
                        {
                            Work();
                        }
                        else
                        {
                            Work();
                        }
                    }
                }

                void Work() { }
            }
            """, InvertIf);
    }

    [Fact]
    public async ValueTask EmptyIfStatementWithElse_IsInverted()
    {
        await Verify.VerifyAsync("""
            class C
            {
                void M(bool b)
                {
                    if (b) [|;|]
                    else Work();
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(bool b)
                {
                    if (!b) Work();
                }

                void Work() { }
            }
            """, InvertIf);
    }

    [Fact]
    public async ValueTask EmptyIfWithoutElse_HasNoFix()
    {
        // The condition may have side effects, so the statement must not simply disappear.
        await Verify.VerifyNoFixAsync("""
            class C
            {
                void M()
                {
                    if (TryWork()) [|{ }|]
                }

                bool TryWork() => true;
            }
            """);
    }

    [Fact]
    public async ValueTask EmptyIfWithUserDefinedTruth_HasNoFix()
    {
        // For types with operator true/false, '!' is not guaranteed to be the negation.
        await Verify.VerifyNoFixAsync("""
            class Tri
            {
                public static bool operator true(Tri t) => true;
                public static bool operator false(Tri t) => true;
            }

            class C
            {
                void M(Tri t)
                {
                    if (t) [|{ }|]
                    else
                    {
                        Work();
                    }
                }

                void Work() { }
            }
            """);
    }

    [Fact]
    public async ValueTask BothEmpty_ElseIsRemoved_IfRemainsForAHuman()
    {
        var test = Verify.Create("""
            class C
            {
                void M(bool b)
                {
                    if (b) [|{ }|]
                    else [|{ }|]
                }
            }
            """, """
            class C
            {
                void M(bool b)
                {
                    if (b) [|{ }|]
                }
            }
            """, RemoveElse);
        // The remaining empty if has no fix and is expected to stay reported.
        test.FixedState.MarkupHandling = Microsoft.CodeAnalysis.Testing.MarkupMode.Allow;
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask FixAll_RemovesEveryEmptyElse()
    {
        var test = Verify.Create("""
            class C
            {
                void M(bool a, bool b)
                {
                    if (a)
                    {
                        Work();
                    }
                    else [|{ }|]

                    if (b)
                    {
                        Work();
                    }
                    else [|{ }|]
                }

                void Work() { }
            }
            """, """
            class C
            {
                void M(bool a, bool b)
                {
                    if (a)
                    {
                        Work();
                    }

                    if (b)
                    {
                        Work();
                    }
                }

                void Work() { }
            }
            """, RemoveElse);
        test.NumberOfFixAllIterations = 1;
        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
