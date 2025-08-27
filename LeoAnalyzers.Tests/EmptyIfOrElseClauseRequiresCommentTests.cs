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
}
