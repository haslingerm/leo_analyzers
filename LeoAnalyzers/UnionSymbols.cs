using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace LeoAnalyzers;

/// <summary>
///     Access to the union APIs of newer compilers. The package keeps its Roslyn 4.12 baseline,
///     so these public properties are read via reflection; on older hosts they are <see langword="null" />.
/// </summary>
internal static class UnionSymbols
{
    internal static readonly Func<ITypeSymbol, bool>? IsUnion = CreateTypeAccessor<Func<ITypeSymbol, bool>>("IsUnion");

    internal static readonly Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>? GetCaseTypes =
        CreateTypeAccessor<Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>>("UnionCaseTypes");

    /// <summary>Unwraps <c>Nullable&lt;TUnion&gt;</c> and returns the union type, or <see langword="null" /> if the type is not a union.</summary>
    internal static ITypeSymbol? AsUnion(ITypeSymbol? type)
    {
        var unionType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
        return unionType is not null && IsUnion is not null && IsUnion(unionType) ? unionType : null;
    }

    private static TDelegate? CreateTypeAccessor<TDelegate>(string propertyName) where TDelegate : Delegate
    {
        var getter = typeof(ITypeSymbol).GetProperty(propertyName)?.GetMethod;
        return getter is null ? null : (TDelegate) Delegate.CreateDelegate(typeof(TDelegate), getter);
    }
}
