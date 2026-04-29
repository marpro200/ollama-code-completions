// Tests for CompletionPostProcessor.
//
// To compile and run standalone (no test framework required):
//   set CSC="C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
//   %CSC% /define:TEST /langversion:9.0 /out:PostProcessorTests.exe CompletionPostProcessor.cs CompletionPostProcessor.Tests.cs
//   PostProcessorTests.exe
//
// The VSIX build does not define TEST, so this file contributes no types and
// the DLL builds cleanly with no entry-point conflicts.

#if TEST
using System;

namespace OllamaCopilot
{
    internal static class CompletionPostProcessorTests
    {
        static int _pass, _fail;

        static int Main()
        {
            // Filter 1 — null / whitespace rejection
            Check("null input",            null,       "", "", "",  null);
            Check("empty input",           "",         "", "", "",  null);
            Check("whitespace-only",       "  \n\t  ", "", "", "",  null);

            // Filter 2 — strip leading newlines on a fresh line
            Check("leading newlines stripped on empty line",
                raw:               "\n\nfoo()",
                lineBeforeCursor:  "    ",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "foo()");

            Check("leading newline preserved when line has content",
                raw:               "\nfoo()",
                lineBeforeCursor:  "bar(",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "\nfoo()");

            // Filter 3 — mid-line → single-line only
            Check("mid-line completion truncated to first line",
                raw:               "foo\nbar",
                lineBeforeCursor:  "  x = ",
                lineAfterCursor:   ";",
                bufferAfterCursor: ";",
                expected:          "foo");

            Check("semicolon after cursor forces single-line",
                raw:               "result\nmore",
                lineBeforeCursor:  "var x = ",
                lineAfterCursor:   ";",
                bufferAfterCursor: ";",
                expected:          "result");

            Check("end-of-line completion allows multi-line",
                raw:               "foo\nbar",
                lineBeforeCursor:  "  x = ",
                lineAfterCursor:   "",
                bufferAfterCursor: "\n",
                expected:          "foo\nbar");

            // Filter 4 — suffix overlap
            Check("suffix overlap basic: bar); + ); → bar",
                raw:               "bar);",
                lineBeforeCursor:  "foo(",
                lineAfterCursor:   "",
                bufferAfterCursor: ");",
                expected:          "bar");

            Check("suffix overlap with CRLF vs LF difference",
                raw:               "bar);\r\n",
                lineBeforeCursor:  "foo(",
                lineAfterCursor:   "",
                bufferAfterCursor: ");\n",
                expected:          "bar");

            Check("whitespace-only overlap is not stripped",
                raw:               "foo  ",
                lineBeforeCursor:  "",
                lineAfterCursor:   "",
                bufferAfterCursor: "  ",
                expected:          "foo  ");

            // Filter 5 — bracket balance
            Check("unmatched closing brace truncated",
                raw:               "foo() }",
                lineBeforeCursor:  "  ",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "foo() ");

            Check("unmatched closing paren truncated",
                raw:               "a, b)",
                lineBeforeCursor:  "foo(",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "a, b");

            Check("bracket inside double-quoted string not counted",
                raw:               "print(\")\") + x",
                lineBeforeCursor:  "",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "print(\")\") + x");

            Check("bracket inside line comment not counted",
                raw:               "x = 1; // fix }",
                lineBeforeCursor:  "",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "x = 1; // fix }");

            Check("balanced brackets pass through",
                raw:               "Foo(a, b[0])",
                lineBeforeCursor:  "",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "Foo(a, b[0])");

            // Filter 6 — prefix echo
            Check("prefix echo stripped",
                raw:               "var x = 5",
                lineBeforeCursor:  "    var x = ",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "5");

            Check("single-char echo not stripped (n < 2)",
                raw:               "=5",
                lineBeforeCursor:  "x =",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          "=5");  // "=" alone is length 1, below threshold

            // Filter 7 — whitespace-only after all trimming
            Check("only-whitespace result discarded",
                raw:               "   ",
                lineBeforeCursor:  "",
                lineAfterCursor:   "",
                bufferAfterCursor: "",
                expected:          null);

            // Interaction: suffix overlap leaves whitespace-only → null
            Check("suffix overlap leaves empty → null",
                raw:               ");",
                lineBeforeCursor:  "foo(",
                lineAfterCursor:   "",
                bufferAfterCursor: ");",
                expected:          null);

            Console.WriteLine($"\n{_pass} passed, {_fail} failed.");
            return _fail == 0 ? 0 : 1;
        }

        static void Check(string name,
                          string raw,
                          string lineBeforeCursor,
                          string lineAfterCursor,
                          string bufferAfterCursor,
                          string expected)
        {
            string actual = CompletionPostProcessor.Clean(raw, lineBeforeCursor, lineAfterCursor, bufferAfterCursor);
            if (actual == expected)
            {
                Console.WriteLine($"  PASS  {name}");
                _pass++;
            }
            else
            {
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        expected: {Fmt(expected)}");
                Console.WriteLine($"        actual:   {Fmt(actual)}");
                _fail++;
            }
        }

        static string Fmt(string s) =>
            s == null ? "<null>" : $"\"{s.Replace("\r", "\\r").Replace("\n", "\\n")}\"";
    }
}
#endif
