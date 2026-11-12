using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LeoAnalyzers.Compiler.Tests;

public sealed class SwitchPatternConstantRewriterTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("(string)null")]
    [InlineData("default(string)")]
    [InlineData("nameof(C)")]
    [InlineData("'\\n'")]
    [InlineData("\"text\\n\\\"quoted\\\"\"")]
    [InlineData("sbyte.MinValue")]
    [InlineData("byte.MaxValue")]
    [InlineData("short.MinValue")]
    [InlineData("ushort.MaxValue")]
    [InlineData("int.MinValue")]
    [InlineData("uint.MaxValue")]
    [InlineData("long.MinValue")]
    [InlineData("ulong.MaxValue")]
    [InlineData("1.2345678f")]
    [InlineData("1.2345678901234567d")]
    [InlineData("1.0000000000000000000000000001m")]
    [InlineData("decimal.MaxValue")]
    [InlineData("float.NaN")]
    [InlineData("float.PositiveInfinity")]
    [InlineData("float.NegativeInfinity")]
    [InlineData("double.NaN")]
    [InlineData("double.PositiveInfinity")]
    [InlineData("double.NegativeInfinity")]
    [InlineData("-0.0f")]
    [InlineData("-0.0d")]
    [InlineData("E.A")]
    [InlineData("Alias.B")]
    public void ResolutionPreservesCompilerConstantValueAndType(string expression)
    {
        var token = TestContext.Current.CancellationToken;
        var tree = CSharpSyntaxTree.ParseText($$"""
            using Alias = E;
            enum E : byte { A = 1, B = 2 }
            class C { object M() => {{expression}}; }
            """, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: token);
        var compilation = CSharpCompilation.Create("ConstantTest", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(tree);
        var original = tree.GetRoot(token).DescendantNodes().OfType<ArrowExpressionClauseSyntax>().Single().Expression;
        var value = model.GetConstantValue(original, token);
        Assert.True(value.HasValue);
        var type = model.GetTypeInfo(original, token).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var marker = new SyntaxAnnotation();
        var resolved = new SwitchPatternConstantRewriter(model, token).ResolveConstant(original).WithAdditionalAnnotations(marker);
        var replacement = tree.WithRootAndOptions(tree.GetRoot(token).ReplaceNode(original, resolved), tree.Options);
        var rewrittenCompilation = compilation.ReplaceSyntaxTree(tree, replacement);
        Assert.Empty(rewrittenCompilation.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error));
        var rewrittenModel = rewrittenCompilation.GetSemanticModel(replacement);
        var rewritten = replacement.GetRoot(token).GetAnnotatedNodes(marker).Single();
        var rewrittenValue = rewrittenModel.GetConstantValue(rewritten, token);
        Assert.True(rewrittenValue.HasValue);
        Assert.Equal(value.Value, rewrittenValue.Value);
        Assert.Equal(type, rewrittenModel.GetTypeInfo(rewritten, token).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        if (value.Value is float single)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(single), BitConverter.SingleToInt32Bits((float)rewrittenValue.Value!));
        }
        if (value.Value is double number)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits((double)rewrittenValue.Value!));
        }
    }
}
