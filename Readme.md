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

## LA0003: Switch statements over union types must be exhaustive

**Warning.** Handle every possible value, including `null` when applicable,
or add a `default` case.

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

LA0003 requires C# 15 union support. It is inactive with the .NET 10 SDK.
