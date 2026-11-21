using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace LeoAnalyzers.CodeFixes.Tests;

/// <summary>
///     Runs a code fix against .NET 11 reference assemblies and the newest language version (unions need both).
///     The test framework also checks that the fixed code compiles and no longer reports the fixed diagnostics.
/// </summary>
internal static class Verifier<TAnalyzer, TCodeFix>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    public static readonly ReferenceAssemblies Net110 = new("net11.0",
        new PackageIdentity("Microsoft.NETCore.App.Ref", "11.0.0"), Path.Combine("ref", "net11.0"));

    public static CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier> Create(string source, string fixedSource,
        string? equivalenceKey = null)
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            ReferenceAssemblies = Net110,
            TestCode = source,
            FixedCode = fixedSource,
            CodeActionEquivalenceKey = equivalenceKey
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var options = (CSharpParseOptions) solution.GetProject(projectId)!.ParseOptions!;
            var compilationOptions = solution.GetProject(projectId)!.CompilationOptions!
                                             .WithSpecificDiagnosticOptions(
                                                 solution.GetProject(projectId)!.CompilationOptions!.SpecificDiagnosticOptions);
            return solution.WithProjectParseOptions(projectId, options.WithLanguageVersion(LanguageVersion.Preview))
                           .WithProjectCompilationOptions(projectId, ((CSharpCompilationOptions) compilationOptions)
                               .WithNullableContextOptions(NullableContextOptions.Enable));
        });
        return test;
    }

    public static Task VerifyAsync(string source, string fixedSource, string? equivalenceKey = null) =>
        Create(source, fixedSource, equivalenceKey).RunAsync(TestContext.Current.CancellationToken);

    /// <summary>The diagnostic is reported, but no fix is offered.</summary>
    public static Task VerifyNoFixAsync(string source) =>
        Create(source, source).RunAsync(TestContext.Current.CancellationToken);
}
