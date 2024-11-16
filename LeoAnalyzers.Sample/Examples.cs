// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace LeoAnalyzers.Sample;

// If you don't see warnings, build the Analyzers Project.

public class Examples
{
   public class PrimaryCtorAssignment(int foo)
   {
      public void Bar()
      {
         foo = 1; // this should be an error
      }

      public void Baz()
      {
         Foobar(out foo); // this should be an error
      }

      private static void Foobar(out int qux)
      {
         qux = -1;
      }
   }
}
