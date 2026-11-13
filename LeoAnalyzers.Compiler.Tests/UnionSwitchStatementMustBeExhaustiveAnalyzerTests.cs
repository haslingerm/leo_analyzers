using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace LeoAnalyzers.Compiler.Tests;

public sealed class UnionSwitchStatementMustBeExhaustiveAnalyzerTests
{
    public static bool SupportsUnions => typeof(ITypeSymbol).GetProperty("IsUnion") is not null;
    private const string RequiresUnions = "Requires an SDK whose Roslyn exposes union types.";

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("", true)]
    [InlineData("case int i: break;", true)]
    [InlineData("case int i: break; case string s: break;", false)]
    [InlineData("case int: case string: break;", false)]
    [InlineData("case int i when i > 0: break; case string: break;", true)]
    [InlineData("case int i when true: break; case string: break;", false)]
    [InlineData("case int i when false: break; case string: break;", true)]
    [InlineData("case int i when i > 0: break; case int: break; case string: break;", false)]
    [InlineData("case int or string: break;", false)]
    [InlineData("case int and > 0: break; case string: break;", true)]
    [InlineData("case int and > 0: break; case int and <= 0: break; case string: break;", false)]
    [InlineData("case string { Length: > 0 }: break; case int: break;", true)]
    [InlineData("case string { Length: >= 0 }: break; case int: break;", false)]
    [InlineData("case 0: break; case string: break;", true)]
    [InlineData("case not int: break;", true)]
    [InlineData("case not int: break; case int: break;", false)]
    [InlineData("case var anything: break;", false)]
    [InlineData("case object anything: break;", false)]
    [InlineData("default: break;", false)]
    [InlineData("default: break; case int: break;", false)]
    public async ValueTask UnionPatterns(string cases, bool expectDiagnostic)
    {
        await VerifyAsync(UnionSource(cases), expectDiagnostic ? 1 : 0);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("case int: break; case string: break;", "Result", true)]
    [InlineData("case int number: break; case string text: break;", "Result", true)]
    [InlineData("case (int): break; case (string): break;", "Result", true)]
    [InlineData("case System.Int32: break; case System.String: break;", "Result", true)]
    [InlineData("case int: break;", "Result", false)]
    [InlineData("case int when true: break; case string: break;", "Result", false)]
    [InlineData("case int or string: break;", "Result", false)]
    [InlineData("case int: break; case string { Length: >= 0 }: break;", "Result", false)]
    [InlineData("case object: break;", "Result", false)]
    [InlineData("case var anything: break;", "Result", true)]
    [InlineData("case var anything when true: break;", "Result", false)]
    [InlineData("default: break;", "Result", true)]
    [InlineData("case int: break; case string: break;", "Result?", false)]
    [InlineData("case int: break; case string: break; case null: break;", "Result?", true)]
    [InlineData("case int: break; case string: break; case null when true: break;", "Result?", false)]
    [InlineData("case var anything: break;", "Result?", true)]
    public void FastProofOnlyAcceptsCertainCoverage(string cases, string inputType, bool expected)
    {
        var compilation = CreateCompilation(UnionSource(cases, inputType));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(expected, HasFastProof(compilation));
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask TypeAliasesUseTheFastProof()
    {
        var source = "using Number = System.Int32; using Text = System.String;\n" +
            UnionSource("case Number: break; case Text: break;");
        Assert.True(HasFastProof(CreateCompilation(source)));
        await VerifyAsync(source, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask ConstructedGenericUnionUsesTheFastProof()
    {
        const string Source = """
            #nullable enable
            public union Result<T>(T, string) where T : struct;
            class C
            {
                void M(Result<int> value)
                {
                    switch (value) { case int: break; case string: break; }
                }
            }
            """;
        Assert.True(HasFastProof(CreateCompilation(Source)));
        await VerifyAsync(Source, 0);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("T, string?", false, false, 1)]
    [InlineData("string?, T", false, false, 1)]
    [InlineData("T, string?", true, false, 0)]
    [InlineData("string?, T", true, false, 0)]
    [InlineData("T, string", false, true, 0)]
    [InlineData("string, T", false, true, 0)]
    public async ValueTask SubstitutedDuplicateCasesCheckEveryConstructor(string memberTypes, bool handlesNull,
        bool expectedFastProof, int expectedCount)
    {
        var source = $$"""
            #nullable enable
            public union Result<T>({{memberTypes}}) where T : class;
            class C
            {
                void M(Result<string> value)
                {
                    switch (value)
                    {
                        case string: break;
                        {{(handlesNull ? "case null: break;" : "")}}
                    }
                }
            }
            """;
        var compilation = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(expectedFastProof, HasFastProof(compilation));
        await VerifyAsync(source, expectedCount);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask UnknownReferenceNullabilityKeepsTheCompilerCheck()
    {
        var source = UnionSource("case int: break; case string: break;").Replace("#nullable enable", "#nullable disable");
        Assert.False(HasFastProof(CreateCompilation(source)));
        await VerifyAsync(source, 0);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async ValueTask NullableReferenceMemberKeepsTheCompilerCheck(bool handlesNull, int expectedCount)
    {
        var source = UnionSource("case int: break; case string: break;" + (handlesNull ? "case null: break;" : ""))
            .Replace("Result(int, string)", "Result(int, string?)");
        Assert.False(HasFastProof(CreateCompilation(source)));
        await VerifyAsync(source, expectedCount);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask OpenGenericUnionKeepsTheCompilerCheck()
    {
        const string Source = """
            public union Result<T>(T, string) where T : struct;
            class C
            {
                void M<T>(Result<T> value) where T : struct
                {
                    switch (value) { case T: break; case string: break; }
                }
            }
            """;
        Assert.False(HasFastProof(CreateCompilation(Source)));
        await VerifyAsync(Source, 0);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("int, string", "int", "string")]
    [InlineData("bool, string", "bool", "string")]
    [InlineData("First, Second", "First", "Second")]
    [InlineData("Base, Derived", "Base", "Derived")]
    [InlineData("(int X, int Y), string", "System.ValueTuple<int, int>", "string")]
    [InlineData("int[], string", "int[]", "string")]
    [InlineData("Inner, bool", "Inner", "bool")]
    [InlineData("InnerNullable, bool", "InnerNullable", "bool")]
    [InlineData("int, string?", "int", "string")]
    [InlineData("System.IDisposable, string", "System.IDisposable", "string")]
    [InlineData("E, string", "E", "string")]
    public async ValueTask OptimizedAnalysisAgreesWithCompiler(string memberTypes, string first, string second)
    {
        // Compile actual switch expressions as an independent oracle. These
        // exercise the original compiler check, rather than a second copy of
        // the analyzer's fast-path implementation.
        string[][] patternSets = [
            [], [first], [second], [first, second], [second, first],
            [first + " when true", second], [first + " when false", second],
            [first + " or " + second], ["var anything"], ["object"],
            ["not " + first], ["not " + first, first]
        ];
        var declarations = $$"""
            #nullable enable
            public sealed class First { }
            public sealed class Second { }
            public class Base { }
            public sealed class Derived : Base { }
            public enum E { A, B }
            public union Inner(int, string);
            public union InnerNullable(int, string?);
            public union Result({{memberTypes}});
            """;
        Assert.Empty(CreateCompilation(declarations).GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        foreach (var nullable in new[] { false, true })
        {
            foreach (var patterns in patternSets)
            {
                var inputType = nullable ? "Result?" : "Result";
                var cases = string.Join(" ", patterns.Select(p => $"case {p}: break;"));
                var arms = string.Join(", ", patterns.Select(p => $"{p} => 0"));
                var source = declarations + "\n" + $$"""
                    class C
                    {
                        void Statement({{inputType}} value)
                        {
                            switch (value) { {{cases}} }
                        }
                        int Expression({{inputType}} value) => value switch { {{arms}} };
                    }
                    """;
                var compilation = CreateCompilation(source);
                var compilerDiagnostics = compilation.GetDiagnostics(TestContext.Current.CancellationToken);
                // Broad type patterns can subsume later labels when union
                // member types overlap. Invalid programs have no coverage
                // contract, and must not produce an additional analyzer warning.
                var hasErrors = compilerDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
                var expected = !hasErrors && compilerDiagnostics.Any(d => IsExhaustivenessDiagnostic(d.Id));
                var diagnostics = await AnalyzeAsync(compilation);
                Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
                Assert.True(diagnostics.Any(d => d.Id == Rules.UnionSwitchStatementMustBeExhaustive) == expected,
                    $"Compiler disagreement for {memberTypes}, {inputType}: {string.Join(", ", patterns)}; " +
                    $"fast proof: {HasFastProof(compilation)}; compiler: {string.Join("; ", compilerDiagnostics)}; analyzer: {string.Join("; ", diagnostics)}");
                if (!hasErrors && HasFastProof(compilation))
                {
                    Assert.False(expected);
                }
            }
        }
    }

    private static bool IsExhaustivenessDiagnostic(string id) => id is "CS8509" or "CS8524" or "CS8655" or "CS8846" or "CS8847";

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("int, string", "string", "Result value = default;")]
    [InlineData("int, string", "string", "Result value = new Result((string)null!);")]
    [InlineData("int, string", "string", "Result value = (string?)null;")]
    [InlineData("int, string", "string", "string? text = null; Result value = text;")]
    [InlineData("int, string", "string", "Result value = 1; value = (string?)null;")]
    [InlineData("int, string", "string", "Result? value = default;")]
    [InlineData("int, bool", "bool", "Result value = default;")]
    [InlineData("int, bool", "bool", "Result? value = default;")]
    public async ValueTask InputFlowAgreesWithCompiler(string memberTypes, string second, string initialization)
    {
        var source = $$"""
            #nullable enable
            public union Result({{memberTypes}});
            class C
            {
                void Statement()
                {
                    {{initialization}}
                    switch (value) { case int: break; case {{second}}: break; }
                }
                int Expression()
                {
                    {{initialization}}
                    return value switch { int => 0, {{second}} => 0 };
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var compilerDiagnostics = compilation.GetDiagnostics(TestContext.Current.CancellationToken);
        Assert.Empty(compilerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var expected = compilerDiagnostics.Any(d => IsExhaustivenessDiagnostic(d.Id));
        var diagnostics = await AnalyzeAsync(compilation);
        Assert.Equal(expected ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal(Rules.UnionSwitchStatementMustBeExhaustive, d.Id));
        if (HasFastProof(compilation)) Assert.False(expected);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("value = (string?)null;", "", 1)]
    [InlineData("", "value = 1;", 0)]
    [InlineData("Assign(out value);", "", 0)]
    public async ValueTask WrittenParameterKeepsTheCompilerCheck(string before, string after, int expectedCount)
    {
        var source = $$"""
            #nullable enable
            public union Result(int, string);
            class C
            {
                static void Assign(out Result value) { value = 1; }
                void M(Result value)
                {
                    {{before}}
                    switch (value) { case int: break; case string: break; }
                    {{after}}
                }
            }
            """;
        Assert.False(HasFastProof(CreateCompilation(source)));
        await VerifyAsync(source, expectedCount);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask CapturedParameterKeepsTheCompilerCheck()
    {
        const string Source = """
            #nullable enable
            public union Result(int, string);
            class C
            {
                void M(Result value)
                {
                    void Local()
                    {
                        switch (value) { case int: break; case string: break; }
                    }
                    Local();
                }
            }
            """;
        Assert.False(HasFastProof(CreateCompilation(Source)));
        await VerifyAsync(Source, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask RefParameterKeepsTheCompilerCheck()
    {
        var source = UnionSource("case int: break; case string: break;").Replace("M(Result value)", "M(ref Result value)");
        Assert.False(HasFastProof(CreateCompilation(source)));
        await VerifyAsync(source, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask ConstructorInitializerKeepsTheCompilerCheck()
    {
        const string Source = """
            #nullable enable
            public union Result(int, string);
            public class B { public B(Result value) { } }
            public class C : B
            {
                public C(Result value) : base(value = (string?)null)
                {
                    switch (value) { case int: break; case string: break; }
                }
            }
            public class Oracle : B
            {
                public Oracle(Result value) : base(value = (string?)null)
                {
                    _ = value switch { int => 0, string => 0 };
                }
            }
            """;
        var compilation = CreateCompilation(Source);
        Assert.False(HasFastProof(compilation));
        var compilerDiagnostics = compilation.GetDiagnostics(TestContext.Current.CancellationToken);
        Assert.Empty(compilerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var expected = compilerDiagnostics.Any(d => IsExhaustivenessDiagnostic(d.Id));
        await VerifyAsync(Source, expected ? 1 : 0);
    }

    private static bool HasFastProof(CSharpCompilation compilation)
    {
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var statement = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<SwitchStatementSyntax>().Single();
        var input = model.GetTypeInfo(statement.Expression, TestContext.Current.CancellationToken);
        var unionType = input.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : input.Type!;
        var caseTypes = (ImmutableArray<ITypeSymbol>)typeof(ITypeSymbol).GetProperty("UnionCaseTypes")!.GetValue(unionType)!;
        return UnionSwitchCoverage.IsObviouslyExhaustive(statement, model, unionType, caseTypes, input, TestContext.Current.CancellationToken);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("case int: break;", 1)]
    [InlineData("case int: break; case string: break; case null: break;", 0)]
    [InlineData("case int: break; case string: break;", 1)]
    [InlineData("case var anything: break;", 0)]
    public async ValueTask NullableUnion(string cases, int expectedCount)
    {
        await VerifyAsync(UnionSource(cases, "Result?"), expectedCount);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask GuardBindsPatternAndExistingLocalVariables()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                void M(Result value, int threshold)
                {
                    switch (value)
                    {
                        case int number when number > threshold: break;
                        case string text when text.Length > threshold: break;
                    }
                }
            }
            """, 1);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask BooleanCasesAreExhaustiveTogether()
    {
        await VerifyAsync("""
            public union Result(bool, string);
            class C
            {
                void M(Result value)
                {
                    switch (value)
                    {
                        case true: break;
                        case false: break;
                        case string: break;
                    }
                }
            }
            """, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask ConstructedGenericUnion()
    {
        await VerifyAsync("""
            public union Result<T>(T, string) where T : struct;
            class C
            {
                void M(Result<int> value)
                {
                    switch (value) { case string: break; }
                }
            }
            """, 1);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask UnionFromMetadata()
    {
        var union = CreateCompilation("public union Result(int, string);");
        using var image = new MemoryStream();
        var emitted = union.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(image.ToArray());
        await VerifyAsync("""
            class C
            {
                void M(Result value)
                {
                    switch (value) { case int: break; }
                }
            }
            """, 1, reference);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async ValueTask ReferenceMemberMetadataKeepsTheCompilerCheck(bool nullableMember, int expectedCount)
    {
        var union = CreateCompilation("#nullable enable\npublic union Result(int, string" + (nullableMember ? "?" : "") + ");");
        using var image = new MemoryStream();
        var emitted = union.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(image.ToArray());
        const string Source = """
            #nullable enable
            class C
            {
                void M(Result value)
                {
                    switch (value) { case int: break; case string: break; }
                }
            }
            """;
        Assert.False(HasFastProof(CreateCompilation(Source, reference)));
        await VerifyAsync(Source, expectedCount, reference);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("// <auto-generated/>\n")]
    [InlineData("#pragma warning disable LA0003\n")]
    public async ValueTask HonorsGeneratedCodeAndSuppression(string prefix)
    {
        await VerifyAsync(prefix + UnionSource("case int: break;"), 0);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("#pragma warning disable CS8509, CS8524, CS8655, CS8846, CS8847\n")]
    [InlineData("#pragma warning disable\n#pragma warning restore LA0003\n")]
    public async ValueTask CompilerWarningPragmasDoNotDisableTheAnalyzer(string prefix)
    {
        await VerifyAsync(prefix + UnionSource("case int: break;"), 1);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask CompilerWarningOptionsDoNotDisableTheAnalyzer()
    {
        var compilation = CreateCompilation(UnionSource("case int: break;"));
        compilation = compilation.WithOptions(compilation.Options
            .WithSpecificDiagnosticOptions(new[] { "CS8509", "CS8524", "CS8655", "CS8846", "CS8847" }
                .ToImmutableDictionary(id => id, _ => ReportDiagnostic.Suppress)));
        var diagnostics = await AnalyzeAsync(compilation);
        Assert.Equal(Rules.UnionSwitchStatementMustBeExhaustive, Assert.Single(diagnostics).Id);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask WarningLevelZeroDisablesTheAnalyzerWarning()
    {
        var compilation = CreateCompilation(UnionSource("case int: break;"));
        Assert.Empty(await AnalyzeAsync(compilation.WithOptions(compilation.Options.WithWarningLevel(0))));
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask NullableUnionNarrowedByFlowDoesNotNeedANullCase()
    {
        const string Source = """
            #nullable enable
            public union Result(int, string);
            class C
            {
                void M(Result? value)
                {
                    if (value is null) return;
                    switch (value)
                    {
                        case int: break;
                        case string: break;
                    }
                }
            }
            """;
        Assert.True(HasFastProof(CreateCompilation(Source)));
        await VerifyAsync(Source, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask WorksInVoidAsyncAndIteratorMethods()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                async System.Threading.Tasks.Task M(Result value)
                {
                    await System.Threading.Tasks.Task.Yield();
                    switch (value) { case int: break; }
                }
                System.Collections.Generic.IEnumerable<int> N(Result value)
                {
                    switch (value) { case string: break; }
                    yield return 0;
                }
            }
            """, 2);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask NestedSwitchesAreCheckedIndependently()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                void M(Result value, Result other)
                {
                    switch (value)
                    {
                        case int:
                            switch (other) { case string: break; }
                            break;
                    }
                }
            }
            """, 2);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask WorksInTopLevelCodeAndLocalFunctions()
    {
        await VerifyAsync("""
            Result value = 1;
            switch (value) { case int: break; }
            void Local(Result other)
            {
                switch (other) { case string: break; }
            }
            Local(value);
            public union Result(int, string);
            """, 2, outputKind: OutputKind.ConsoleApplication);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask SwitchExpressionsRemainTheCompilersResponsibility()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                int M(Result value) => value switch { int => 0 };
            }
            """, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask IncompleteCodeDoesNotCrash()
    {
        var source = UnionSource("case MissingType missing: break;");
        var diagnostics = await AnalyzeAsync(CreateCompilation(source));
        Assert.Empty(diagnostics);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async ValueTask LoopCarriedNullStateIncludesSwitchBody(bool handlesNull, int expectedCount)
    {
        await VerifyAsync($$"""
            #nullable enable
            public union Result(int, string);
            class C
            {
                void M(bool keepGoing)
                {
                    Result? value = 1;
                    while (keepGoing)
                    {
                        switch (value)
                        {
                            case int: value = null; break;
                            case string: break;
                            {{(handlesNull ? "case null: break;" : "")}}
                        }
                    }
                }
            }
            """, expectedCount);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async ValueTask GuardCanReferenceFunctionDeclaredInSwitchSection(bool handlesString, int expectedCount)
    {
        await VerifyAsync($$"""
            public union Result(int, string);
            class C
            {
                void M(Result value)
                {
                    switch (value)
                    {
                        case int number when Accept(number):
                            static bool Accept(int number) => number > 0;
                            break;
                        case int: break;
                        {{(handlesString ? "case string: break;" : "")}}
                    }
                }
            }
            """, expectedCount);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async ValueTask NestedGuardSwitchWarningIsNotAttributedToOuterStatement(bool exhaustiveOuter, int expectedCount)
    {
        await VerifyAsync($$"""
            public union Result(int, string);
            class C
            {
                void M(Result value, Result other)
                {
                    switch (value)
                    {
                        case int when other switch { int => true }: break;
                        {{(exhaustiveOuter ? "case int or string: break;" : "case int: break;")}}
                    }
                }
            }
            """, expectedCount);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask NestedInputSwitchWarningIsNotAttributedToOuterStatement()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                void M(Result value)
                {
                    switch (value switch { int number => new Result(number) })
                    {
                        case int or string: break;
                    }
                }
            }
            """, 0);
    }

    [Fact(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    public async ValueTask CaseCanReferenceConstantFromPreviousSwitchSection()
    {
        await VerifyAsync("""
            public union Result(int, string);
            class C
            {
                void M(Result value)
                {
                    switch (value)
                    {
                        case string:
                            const int Number = 0;
                            break;
                        case Number: break;
                    }
                }
            }
            """, 1);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("nameof(number)", false, 1)]
    [InlineData("nameof(number)", true, 0)]
    [InlineData("nameof(number) + \"!\"", false, 1)]
    [InlineData("string and nameof(number)", false, 1)]
    [InlineData("string { Length: > 0 } and nameof(number)", false, 1)]
    [InlineData("string { Length: > (nameof(number) == \"number\" ? 0 : 1) }", false, 1)]
    public async ValueTask SharedSectionConstantsKeepTheirOriginalScope(string constantPattern, bool handlesAllStrings, int expectedCount)
    {
        await VerifyAsync($$"""
            public union Result(int, string);
            class C
            {
                void M(Result value)
                {
                    switch (value)
                    {
                        case int number:
                        case {{constantPattern}}:
                            break;
                        {{(handlesAllStrings ? "case string: break;" : "")}}
                    }
                }
            }
            """, expectedCount);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async ValueTask NullableDirectiveInCaseBodyDoesNotChangeInputCoverage(bool nullableEnabled, int expectedCount)
    {
        await VerifyAsync($$"""
            #nullable {{(nullableEnabled ? "enable" : "disable")}}
            public union Result(int, string);
            class C
            {
                void M(Result? value)
                {
                    switch (value)
                    {
                        case int:
            #nullable {{(nullableEnabled ? "disable" : "enable")}}
                            break;
                        case string: break;
                    }
                }
            }
            """, expectedCount);
    }

    [Theory(SkipUnless = nameof(SupportsUnions), Skip = RequiresUnions)]
    [InlineData("", "int NAME = 0; SWITCH")]
    [InlineData("", "SWITCH int NAME = 0;")]
    [InlineData("", "SWITCH void L() { int NAME = 0; }")]
    [InlineData("", "System.Action<int> a = NAME => { SWITCH };")]
    [InlineData("int NAME;", "SWITCH")]
    [InlineData("int NAME;", "switch (value) { case int when NAME > 0: break; case int: break; MORE }")]
    [InlineData("", "switch (value) { case int: int NAME = 0; break; MORE }")]
    [InlineData("", "switch (value) { case int NAME: break; MORE }")]
    public async ValueTask ProbeVariableNameAvoidsExistingNames(string member, string body)
    {
        // The probe variable name contains the switch position. Code that
        // already uses that exact name must not change the analysis result.
        foreach (var (more, expectedCount) in new[] { ("", 1), ("case string: break;", 0) })
        {
            var template = $$"""
                #nullable enable
                public union Result(int, string);
                class C
                {
                    {{member}}
                    void M(Result value)
                    {
                        {{body.Replace("SWITCH", "switch (value) { case int: break; MORE }").Replace("MORE", more)}}
                    }
                }
                """;
            var source = template;
            var position = -1;
            while (source.IndexOf("switch (value)", StringComparison.Ordinal) != position)
            {
                position = source.IndexOf("switch (value)", StringComparison.Ordinal);
                source = template.Replace("NAME", "__LA0003_input_" + position);
            }

            await VerifyAsync(source, expectedCount);
        }
    }

    [Theory]
    [InlineData("int", "case 1: break;")]
    [InlineData("object", "case int: break;")]
    [InlineData("E", "case E.A: break;")]
    public async ValueTask OrdinaryTypesAreIgnored(string type, string cases)
    {
        await VerifyAsync($$"""
            enum E { A, B }
            class C
            {
                void M({{type}} value)
                {
                    switch (value) { {{cases}} }
                }
            }
            """, 0);
    }

    [Fact]
    public async ValueTask ExistingRulesStillRunInTheSdkCompilerHost()
    {
        var compilation = CreateCompilation("""
            class C(int value)
            {
                void M(bool condition)
                {
                    value = 1;
                    if (condition) { }
                }
            }
            """);
        var diagnostics = await compilation.WithAnalyzers([
            new PrimaryConstructorParameterMustBeReadOnlyAnalyzer(),
            new EmptyIfOrElseClauseRequiresCommentAnalyzer(),
            new UnionSwitchStatementMustBeExhaustiveAnalyzer()
        ], new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, true, false, false))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { Rules.PrimaryConstructorParameterShouldBeReadOnly, Rules.EmptyIfOrElseClauseRequiresComment },
            diagnostics.Select(d => d.Id).OrderBy(id => id));
    }

    private static string UnionSource(string cases, string type = "Result") => $$"""
        #nullable enable
        public union Result(int, string);
        class C
        {
            void M({{type}} value)
            {
                switch (value) { {{cases}} }
            }
        }
        """;

    private static CSharpCompilation CreateCompilation(string source, MetadataReference? reference = null,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var references = ImmutableArray.Create<MetadataReference>(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (reference is not null)
        {
            references = references.Add(reference);
        }

        return CSharpCompilation.Create("AnalyzerTest", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(outputKind));
    }

    private static async ValueTask VerifyAsync(string source, int expectedCount, MetadataReference? reference = null,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var compilation = CreateCompilation(source, reference, outputKind);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostics = await AnalyzeAsync(compilation);
        Assert.Equal(expectedCount, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(Rules.UnionSwitchStatementMustBeExhaustive, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Contains("Result", diagnostic.GetMessage());
            Assert.Equal("switch", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        });
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(CSharpCompilation compilation) =>
        compilation.WithAnalyzers([new UnionSwitchStatementMustBeExhaustiveAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
}
