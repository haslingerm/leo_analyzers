using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace LeoAnalyzers.Tests;

public sealed class EmptyIfOrElseClauseRequiresCommentTests
{
    private static CSharpAnalyzerTest<EmptyIfOrElseClauseRequiresCommentAnalyzer, DefaultVerifier> Create(string src) =>
        new()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
            TestCode = src
        };

    [Fact]
    public async ValueTask EmptyIfBlock()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) [|{ }|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask EmptyElseBlock()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) {
                                        // some comment
                                   }
                                   else [|{ }|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask EmptyIfSemicolon()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) [|;|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask EmptyElseSemicolon()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) {
                                        // some comment
                                   }
                                   else [|;|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask EmptyIfBlock_WithComment()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) { /* ok */ }
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask EmptyElseBlock_WithComment_NoDiagnostic()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) {
                                        // some comment
                                   }
                                   else { // ok
                                   }
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask NonEmptyBlocks()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b, int x)
                               {
                                   if (b) { x++; }
                                   else { x--; }
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask ElseIf()
    {
        const string Src = """
                           class C
                           {
                               void M(int n)
                               {
                                   if (n == 0) { /* comment */ }
                                   else if (n == 1) [|{ }|]
                                   else if (n == 2) { /* comment */ }
                                   else [|{ }|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask CommentOnOwnLineBeforeSemicolon_NoDiagnostic()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b)
                                       // intentionally empty
                                       ;
                                   else
                                       /* nothing to do */ ;
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask DocumentationStyleComment_NoDiagnostic()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b)
                                   {
                                       /// intentionally empty
                                   }
                                   else
                                       /// intentionally empty
                                       ;
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask CommentAfterClosingBrace_NoDiagnostic()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) { } // intentionally empty
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask CommentOnConditionLineDoesNotBelongToClause()
    {
        // Matches the Readme: an annotation after the condition does not
        // explain the empty clause itself.
        const string Src = """
                           class C
                           {
                               void M(bool b)
                               {
                                   if (b) // annotation
                                   [|{
                                   }|]
                                   else /* annotation */ [|;|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask InactivePreprocessorCode_NoDiagnostic()
    {
        const string Src = """
                           class C
                           {
                               void M(bool b, int x)
                               {
                                   if (b)
                                   {
                           #if UNDEFINED_SYMBOL
                                       x++;
                           #endif
                                   }
                                   else
                           #if UNDEFINED_SYMBOL
                                       x--;
                           #else
                                       ;
                           #endif
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    // An inactive branch holding only whitespace is empty in every
    // configuration, so it must not count as content.
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("        \n")]
    [InlineData("\t  \n\n   \n")]
    public async ValueTask EmptyPreprocessorBranch_IsStillEmpty(string inactive)
    {
        var src = "class C\n{\n    void M(bool b)\n    {\n" +
                  "        if (b)\n        [|{\n#if UNDEFINED_SYMBOL\n" + inactive + "#endif\n        }|]\n" +
                  "        else\n#if UNDEFINED_SYMBOL\n" + inactive + "#else\n            [|;|]\n#endif\n" +
                  "    }\n}\n";
        await Create(src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask NestedIfInsideEmptyCheckedClause()
    {
        const string Src = """
                           class C
                           {
                               void M(bool a, bool b)
                               {
                                   if (a)
                                   {
                                       if (b) [|{ }|]
                                   }
                                   else [|{ }|]
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async ValueTask LambdaAndLocalFunctionBodies()
    {
        const string Src = """
                           using System;

                           class C
                           {
                               Action<bool> A = b => { if (b) [|{ }|] };

                               void M()
                               {
                                   void L(bool b)
                                   {
                                       if (b) [|;|]
                                   }
                               }
                           }
                           """;
        await Create(Src).RunAsync(TestContext.Current.CancellationToken);
    }
}
