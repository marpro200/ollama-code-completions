// Tests for FileHeaderBuilder pure functions (GetDisplayPath, GetCommentStart, FormatHeader).
//
// To compile and run standalone:
//   set CSC="C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
//   %CSC% /define:TEST /langversion:9.0 /out:FileHeaderTests.exe FileHeaderBuilder.cs FileHeaderBuilder.Tests.cs
//   FileHeaderTests.exe
//
// The #if !TEST guard in FileHeaderBuilder.cs excludes all VS-specific code so no
// VS assemblies are needed for this compilation.

#if TEST
using System;
using System.IO;

namespace OllamaCopilot
{
    internal static class FileHeaderBuilderTests
    {
        static int _pass, _fail;

        static int Main()
        {
            // ── GetDisplayPath ────────────────────────────────────────────────────

            // File inside solution root → relative path with forward slashes
            Eq("file inside solution root",
                FileHeaderBuilder.GetDisplayPath(
                    @"C:\Repo\src\Services\UserService.cs",
                    @"C:\Repo"),
                "src/Services/UserService.cs");

            // File outside solution root → bare filename
            Eq("file outside solution root",
                FileHeaderBuilder.GetDisplayPath(
                    @"D:\OtherProject\File.cs",
                    @"C:\Repo"),
                "File.cs");

            // No solution open (null root) → bare filename
            Eq("no solution open",
                FileHeaderBuilder.GetDisplayPath(
                    @"C:\SomePath\MyFile.py",
                    null),
                "MyFile.py");

            // Empty string root → bare filename
            Eq("empty solution root",
                FileHeaderBuilder.GetDisplayPath(
                    @"C:\SomePath\MyFile.py",
                    ""),
                "MyFile.py");

            // Case-insensitive comparison on Windows
            Eq("case-insensitive solution root",
                FileHeaderBuilder.GetDisplayPath(
                    @"c:\repo\src\File.cs",
                    @"C:\Repo"),
                "src/File.cs");

            // Solution root with trailing separator is handled correctly
            Eq("solution root with trailing backslash",
                FileHeaderBuilder.GetDisplayPath(
                    @"C:\Repo\Module\File.cs",
                    @"C:\Repo\"),
                "Module/File.cs");

            // "C:\Repo" must not match "C:\Repository\..."
            Eq("prefix-of-root not a false match",
                FileHeaderBuilder.GetDisplayPath(
                    @"C:\Repository\File.cs",
                    @"C:\Repo"),
                "File.cs");

            // Path longer than 200 chars → truncated to last 200
            {
                string manyAs  = new string('a', 195);
                // Relative portion will be 195 'a' chars + "\File.cs" = 203 chars → capped to 200
                string result = FileHeaderBuilder.GetDisplayPath(
                    @"C:\Repo\" + manyAs + @"\File.cs",
                    @"C:\Repo");
                Eq("path > 200 chars is capped at 200 chars", result.Length, 200);
            }

            // Null full path → null
            Eq("null full path returns null",
                FileHeaderBuilder.GetDisplayPath(null, @"C:\Repo"),
                null);

            // Empty full path → null
            Eq("empty full path returns null",
                FileHeaderBuilder.GetDisplayPath("", @"C:\Repo"),
                null);

            // ── GetCommentStart + FormatHeader ────────────────────────────────────

            Eq(".cs uses //",
                FileHeaderBuilder.FormatHeader("foo/Bar.cs",
                    FileHeaderBuilder.GetCommentStart("cs")),
                "// File: foo/Bar.cs\n");

            Eq(".ts uses //",
                FileHeaderBuilder.FormatHeader("src/index.ts",
                    FileHeaderBuilder.GetCommentStart("ts")),
                "// File: src/index.ts\n");

            Eq(".py uses #",
                FileHeaderBuilder.FormatHeader("scripts/run.py",
                    FileHeaderBuilder.GetCommentStart("py")),
                "# File: scripts/run.py\n");

            Eq(".yaml uses #",
                FileHeaderBuilder.FormatHeader("docker-compose.yaml",
                    FileHeaderBuilder.GetCommentStart("yaml")),
                "# File: docker-compose.yaml\n");

            Eq(".sql uses --",
                FileHeaderBuilder.FormatHeader("queries/get.sql",
                    FileHeaderBuilder.GetCommentStart("sql")),
                "-- File: queries/get.sql\n");

            Eq(".lua uses --",
                FileHeaderBuilder.FormatHeader("lib/util.lua",
                    FileHeaderBuilder.GetCommentStart("lua")),
                "-- File: lib/util.lua\n");

            Eq(".vb uses '",
                FileHeaderBuilder.FormatHeader("Module1.vb",
                    FileHeaderBuilder.GetCommentStart("vb")),
                "' File: Module1.vb\n");

            Eq(".html uses <!-- -->",
                FileHeaderBuilder.FormatHeader("Views/Home.html",
                    FileHeaderBuilder.GetCommentStart("html")),
                "<!-- File: Views/Home.html -->\n");

            Eq(".xaml uses <!-- -->",
                FileHeaderBuilder.FormatHeader("Views/Main.xaml",
                    FileHeaderBuilder.GetCommentStart("xaml")),
                "<!-- File: Views/Main.xaml -->\n");

            Eq(".xml uses <!-- -->",
                FileHeaderBuilder.FormatHeader("config.xml",
                    FileHeaderBuilder.GetCommentStart("xml")),
                "<!-- File: config.xml -->\n");

            Eq("unknown extension defaults to //",
                FileHeaderBuilder.FormatHeader("data.xyz",
                    FileHeaderBuilder.GetCommentStart("xyz")),
                "// File: data.xyz\n");

            // FormatHeader with null/empty displayPath → null
            Eq("FormatHeader null displayPath returns null",
                FileHeaderBuilder.FormatHeader(null, "//"),
                null);
            Eq("FormatHeader empty displayPath returns null",
                FileHeaderBuilder.FormatHeader("", "//"),
                null);

            Console.WriteLine($"\n{_pass} passed, {_fail} failed.");
            return _fail == 0 ? 0 : 1;
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        static void Eq(string name, string actual, string expected)
        {
            Report(name, actual == expected,
                expected == null ? "<null>" : $"\"{expected}\"",
                actual   == null ? "<null>" : $"\"{actual}\"");
        }

        static void Eq(string name, int actual, int expected)
        {
            Report(name, actual == expected, expected.ToString(), actual.ToString());
        }

        static void Report(string name, bool ok, string expected, string actual)
        {
            if (ok)
            {
                Console.WriteLine($"  PASS  {name}");
                _pass++;
            }
            else
            {
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        expected: {expected}");
                Console.WriteLine($"        actual:   {actual}");
                _fail++;
            }
        }
    }
}
#endif
