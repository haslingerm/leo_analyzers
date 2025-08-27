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

   public class EmptyIfElse
   {
      public void Test(int x)
      {
         // warning for empty if-clause
         if (x > 0)
         {
         }

         if (x < 0)
         {
            // contains comment, so no warning
         }

         if (x == 0)
         {
            // fine
         }
         // warning for empty else-clause
         else
         {

         }


         if (x == 0)
         {
            // fine
         }
         else
         {
            // fine
         }

         // else-if warning
         if (x > 0)
         {
            // fine
         } else if (x < 0)
         {
         }
      }
   }
}
