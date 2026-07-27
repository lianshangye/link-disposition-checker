using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using LinkDispositionChecker;

internal static class ExcelWritebackTests
{
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0])) return 2;
        string directory = Path.Combine(Path.GetTempPath(), "LinkCheckerWriteback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, Path.GetFileName(args[0]));
        try
        {
            File.Copy(args[0], copy);
            List<ExcelSheetPlan> plans = OpenXmlExcelBridge.LoadPlans(copy);
            ExcelSheetPlan originalPlan = plans.First(plan => plan.Sources.Any(item => !item.ManualOnly && !String.IsNullOrWhiteSpace(item.Url)));
            ExcelLinkSource excelSource = originalPlan.Sources.First(item => !item.ManualOnly && !String.IsNullOrWhiteSpace(item.Url));
            var testPlan = new ExcelSheetPlan
            {
                SheetName = originalPlan.SheetName,
                HeaderRow = originalPlan.HeaderRow,
                LinkColumn = originalPlan.LinkColumn,
                ResultStartColumn = originalPlan.ResultStartColumn,
                Sources = new List<ExcelLinkSource> { excelSource }
            };
            var result = new CheckResult
            {
                Number = 1,
                OriginalUrl = excelSource.Url,
                SourceSheet = originalPlan.SheetName,
                SourceRow = excelSource.Row,
                Verdict = "仍可访问",
                CheckedAt = "2026-07-24 20:00:00"
            };

            string backup = OpenXmlExcelBridge.WriteResults(copy, new List<ExcelSheetPlan> { testPlan }, new[] { result });
            List<ExcelSheetPlan> reopened = OpenXmlExcelBridge.LoadPlans(copy);
            bool sourceSurvived = reopened.SelectMany(plan => plan.Sources)
                .Any(item => item.Row == excelSource.Row && item.Url == excelSource.Url && item.ExpectedTitle == excelSource.ExpectedTitle);
            string writtenVerdict = ReadCell(copy, testPlan.SheetName, excelSource.Row, testPlan.ResultStartColumn);
            bool resultSurvived = writtenVerdict == "有效";
            bool passed = File.Exists(backup) && sourceSurvived && resultSurvived;
            Console.WriteLine((passed ? "PASS " : "FAIL ") + "Excel 隔离副本写回后可重新打开且结果仍在");
            if (!passed)
                Console.WriteLine("backup=" + File.Exists(backup) + ", source=" + sourceSurvived +
                    ", verdict=" + writtenVerdict);
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL Excel writeback: " + ex.GetType().Name + " / " + ex.Message);
            return 3;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static string ReadCell(string path, string sheetName, int row, int column)
    {
        using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            MethodInfo method = typeof(OpenXmlExcelBridge).GetMethod("ReadSheetPaths", BindingFlags.NonPublic | BindingFlags.Static);
            var paths = (Dictionary<string, string>)method.Invoke(null, new object[] { archive });
            ZipArchiveEntry entry = archive.GetEntry(paths[sheetName]);
            using (Stream sheetStream = entry.Open())
            {
                XDocument document = XDocument.Load(sheetStream);
                string reference = ColumnName(column) + row;
                XElement cell = document.Descendants(MainNs + "c")
                    .FirstOrDefault(item => String.Equals((string)item.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
                return cell == null ? "" : String.Concat(cell.Descendants(MainNs + "t").Select(item => item.Value));
            }
        }
    }

    private static string ColumnName(int column)
    {
        string result = "";
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }
}
