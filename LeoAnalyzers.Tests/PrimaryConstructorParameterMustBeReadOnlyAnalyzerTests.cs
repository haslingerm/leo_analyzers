using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace LeoAnalyzers.Tests
{
    public sealed class PrimaryConstructorParameterMustBeReadOnlyAnalyzerTests
    {
        [Fact]
        public async Task ParameterReassignment_Simple()
        {
            const string SrcText = """
                                   class Foo(int bar)
                                   {
                                       public void Baz()
                                       {
                                           [|bar|] = 1;
                                       }
                                   }
                                   """;

            var context = new CSharpAnalyzerTest<PrimaryConstructorParameterMustBeReadOnlyAnalyzer, DefaultVerifier>
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
                TestCode = SrcText
            };

            await context.RunAsync();
        }
    }
}
