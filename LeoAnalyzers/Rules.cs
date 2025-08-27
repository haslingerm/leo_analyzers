using System.Collections.Generic;

namespace LeoAnalyzers;

public static class Rules
{
    public const string PrimaryConstructorParameterShouldBeReadOnly = "LA0001";
    public const string EmptyIfOrElseClauseMustBeCommented = "LA0002";
    
    public static class Categories
    {
        public const string Design = "Design";
    }
}
