using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace LeoAnalyzers.Tests
{
    public sealed class PrimaryConstructorParameterMustBeReadOnlyAnalyzerTests
    {
        private static Task VerifyAsync(string source)
        {
            var context = new CSharpAnalyzerTest<PrimaryConstructorParameterMustBeReadOnlyAnalyzer, DefaultVerifier>
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
                TestCode = source
            };

            return context.RunAsync(TestContext.Current.CancellationToken);
        }

        private static Task VerifyMethodBodyAsync(string body) => VerifyAsync($$"""
            #nullable enable
            class Foo(int bar, int baz, string? text)
            {
                public void Baz(bool condition)
                {
                    {{body}}
                }

                private static void Write(ref int value) => value = 0;
                private static void Read(in int value) { }
                private static void ReadOnlyRef(ref readonly int value) { }
            }
            """);

        [Fact]
        public async ValueTask ParameterReassignment_Simple()
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

            await VerifyAsync(SrcText);
        }

        [Theory]
        [InlineData("[|bar|] += 1;")]
        [InlineData("[|bar|] <<= 1;")]
        [InlineData("[|text|] ??= \"\";")]
        [InlineData("[|bar++|];")]
        [InlineData("[|++bar|];")]
        [InlineData("[|bar--|];")]
        [InlineData("[|--bar|];")]
        [InlineData("var x = [|bar|] = 2;")]
        public async ValueTask Assignments(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Theory]
        [InlineData("([|bar|], [|baz|]) = (1, 2);")]
        [InlineData("int other; ([|bar|], other) = (1, 2);")]
        [InlineData("int other; ([|bar|], (other, [|baz|])) = (1, (2, 3));")]
        [InlineData("([|bar|], var other) = (1, 2);")]
        public async ValueTask Deconstruction(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Theory]
        [InlineData("Write(ref [|bar|]);")]
        [InlineData("int.TryParse(\"1\", out [|bar|]);")]
        [InlineData("System.Threading.Interlocked.Increment(ref [|bar|]);")]
        public async ValueTask WritableReferenceArguments(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Theory]
        [InlineData("Read(in bar);")]
        [InlineData("Read(bar);")]
        [InlineData("ReadOnlyRef(ref bar);")]
        [InlineData("System.Console.WriteLine(bar + baz);")]
        public async ValueTask ReadOnlyUses(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Theory]
        [InlineData("ref int alias = ref [|bar|]; alias = 1;")]
        [InlineData("int local = 0; ref int alias = ref local; alias = ref [|bar|];")]
        [InlineData("ref int alias = ref (condition ? ref [|bar|] : ref [|baz|]);")]
        [InlineData("int local = 0; ref int alias = ref (condition ? ref local : ref [|baz|]);")]
        [InlineData("Write(ref condition ? ref [|bar|] : ref [|baz|]);")]
        public async ValueTask WritableReferences(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Theory]
        [InlineData("ref readonly int alias = ref bar;")]
        [InlineData("int local = 0; ref readonly int alias = ref local; alias = ref bar;")]
        [InlineData("ref readonly int alias = ref (condition ? ref bar : ref baz);")]
        public async ValueTask ReadOnlyReferences(string body)
        {
            await VerifyMethodBodyAsync(body);
        }

        [Fact]
        public async ValueTask LambdasAndLocalFunctions()
        {
            const string Src = """
                               using System;

                               class Foo(int bar)
                               {
                                   private Action Reset => () => [|bar|] = 0;

                                   public void Baz()
                                   {
                                       void Decrement() => [|bar--|];
                                       Func<int> next = delegate { return [|++bar|]; };
                                   }
                               }
                               """;

            await VerifyAsync(Src);
        }

        [Fact]
        public async ValueTask InitializersAndStructs()
        {
            const string Src = """
                               struct Foo(int bar)
                               {
                                   private int _value = [|bar|] = 1;

                                   public void Baz()
                                   {
                                       [|bar|] *= 2;
                                   }
                               }
                               """;

            await VerifyAsync(Src);
        }

        [Fact]
        public async ValueTask Records()
        {
            const string Src = """
                               record Foo(int Bar)
                               {
                                   public int Next { get; } = [|Bar++|];
                               }

                               record struct Baz(string Text)
                               {
                                   public string Trimmed { get; } = [|Text|] = Text.Trim();
                               }
                               """;

            await VerifyAsync(Src);
        }

        [Fact]
        public async ValueTask RecordPropertiesAreNotParameters()
        {
            const string Src = """
                               record struct Foo(int Bar)
                               {
                                   public void Reset() => Bar = 0;
                               }
                               """;

            await VerifyAsync(Src);
        }

        [Fact]
        public async ValueTask OrdinaryParametersMayBeReassigned()
        {
            const string Src = """
                               class Foo(int bar)
                               {
                                   public Foo(string text) : this(text.Length)
                                   {
                                       text = "";
                                   }

                                   public void Baz(int value, ref int other)
                                   {
                                       value = bar;
                                       value++;
                                       ref int alias = ref value;
                                       alias = ref other;
                                   }
                               }
                               """;

            await VerifyAsync(Src);
        }
    }
}
