using System;
using System.Linq;
using LinkDispositionChecker;

internal static class ExcelImportTests
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        try
        {
            var plans = OpenXmlExcelBridge.LoadPlans(args[0]);
            var sources = plans.SelectMany(plan => plan.Sources).ToList();
            int authors = sources.Count(source => !String.IsNullOrWhiteSpace(source.ExpectedAuthor));
            Console.WriteLine("Excel sheets=" + plans.Count + ", sources=" + sources.Count + ", authors=" + authors +
                ", first-author=" + (sources.FirstOrDefault(source => !String.IsNullOrWhiteSpace(source.ExpectedAuthor)) == null
                    ? ""
                    : sources.First(source => !String.IsNullOrWhiteSpace(source.ExpectedAuthor)).ExpectedAuthor));
            return plans.Count > 0 && sources.Count > 0 && authors > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 3;
        }
    }
}
