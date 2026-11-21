# LeoAnalyzers

Roslyn analyzers used at HTL Leonding to encourage safe, clear C# code.

## LA0001: Primary constructor parameters must not be reassigned

**Error.** Use a field or property for mutable state.

```csharp
class Counter(int initialValue)
{
    void Reset()
    {
        initialValue = 0; // LA0001
    }
}
```

**Code fix:** *Introduce private field*: adds `private int _initialValue = initialValue;` and makes all members
use the field. Initializers and the base constructor call keep using the parameter, as they run during construction.
No fix is offered when the parameter is written during construction (initializers, base call), when a lambda in an
initializer captures it, when inactive `#if` code uses it, or for records. This fix does not support *fix all* (and therefore `dotnet format`): apply it per parameter.

## LA0002: Empty if/else clauses require a comment

**Warning.** Remove empty clauses or explain why they are empty.

```csharp
if (ready) // LA0002: empty block
{
}
else
{
    // Intentionally no action here.
}
```

**Code fixes:** *Remove empty else*, and for an empty `if` with a non-empty `else`: *Invert condition and remove empty if*
(`if (!ready) { ... }`; only for plain `bool` conditions, comparisons are negated as a whole, never flipped).
If removing the `else` would let an enclosing `else` bind to this `if`, the result is wrapped in a block. No fix is offered
when the removed parts contain preprocessor directives. There is deliberately no fix that inserts a comment, and none
for an empty `if` without `else`: that is a decision for a human.

## LA0003: Switch statements over union types must be exhaustive

**Warning.** Handle every possible value, including `null` when applicable,
or add a `default` case that handles the remaining values on purpose.

```csharp
void Handle(Result result)
{
    switch (result) // LA0003: missing string case
    {
        case int number:
            System.Console.WriteLine(number);
            break;
    }
}

public union Result(int, string);
```

The compiler checks switch *expressions* over unions, but not switch *statements*, and its flow analysis does not
know that a complete switch statement cannot fall through. A value-returning switch statement therefore needs a
guard. A catch-all whose only statement is a `throw` is such a guard: it handles no case, so LA0003 still checks
every case (it also covers `default(Result)`, which holds no case):

```csharp
int Length(Result result)
{
    switch (result)
    {
        case int number:
            return number;
        case string text:
            return text.Length;
        default:
            throw new UnreachableException(); // guard, not coverage: a missing case is still reported
    }
}
```

Any other `default` (or `case var x:`) is treated as deliberately handling the remaining values.

**Code fix:** *Add missing cases*: adds `case T: throw new NotImplementedException();` for every case type that is
not handled yet, after the existing cases (a trailing guard stays last), and `case null:` where null is what is
missing. A section is only added if it does not cause any compiler error. Not offered for switches containing `#if` regions.

LA0003 requires C# 15 union support. It is inactive with the .NET 10 SDK.
