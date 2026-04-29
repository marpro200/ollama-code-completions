// Tests for CompletionCache.
//
// To compile and run standalone:
//   set CSC="C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
//   %CSC% /define:TEST /langversion:9.0 /out:CacheTests.exe CompletionCache.cs CompletionCache.Tests.cs
//   CacheTests.exe
//
// The VSIX build does not define TEST, so this file contributes no types.

#if TEST
using System;

namespace OllamaCopilot
{
    internal static class CompletionCacheTests
    {
        static int _pass, _fail;

        static int Main()
        {
            Test_ExactHit();
            Test_ExactMiss();
            Test_LruEviction();
            Test_LruPromotion();
            Test_CollisionDefense();
            Test_ExtensionHit();
            Test_ExtensionMissWrongChar();
            Test_ExtensionMissDeltaTooLong();
            Test_ExtensionMissSuffixMismatch();
            Test_ExtensionSuffixIsSubsuffix();
            Test_Clear();
            Test_DuplicateStoreUpdatesNotDuplicates();

            Console.WriteLine($"\n{_pass} passed, {_fail} failed.");
            return _fail == 0 ? 0 : 1;
        }

        // ── individual tests ──────────────────────────────────────────────────────

        static void Test_ExactHit()
        {
            var c = new CompletionCache();
            c.Store("ab", "cd", "X");
            Eq("exact hit", c.TryGetExact("ab", "cd"), "X");
        }

        static void Test_ExactMiss()
        {
            var c = new CompletionCache();
            Eq("exact miss on empty cache", c.TryGetExact("ab", "cd"), null);
            c.Store("ab", "cd", "X");
            Eq("exact miss with wrong prefix", c.TryGetExact("ab", ""),   null);
            Eq("exact miss with wrong suffix", c.TryGetExact("",   "cd"), null);
        }

        static void Test_LruEviction()
        {
            var c = new CompletionCache(capacity: 3);
            c.Store("p1", "s1", "v1");
            c.Store("p2", "s2", "v2");
            c.Store("p3", "s3", "v3");
            c.Store("p4", "s4", "v4"); // should evict p1 (LRU tail)
            Eq("evicted entry is null",    c.TryGetExact("p1", "s1"), null);
            Eq("newest entry is retained", c.TryGetExact("p4", "s4"), "v4");
            Eq("mid entries retained",     c.TryGetExact("p2", "s2"), "v2");
        }

        static void Test_LruPromotion()
        {
            var c = new CompletionCache(capacity: 3);
            c.Store("p1", "", "v1");
            c.Store("p2", "", "v2");
            c.Store("p3", "", "v3"); // LRU order: [p3, p2, p1]
            c.TryGetExact("p1", "");  // promote p1 → [p1, p3, p2]
            c.Store("p4", "", "v4"); // evict LRU tail = p2 → [p4, p1, p3]
            Eq("promoted entry survives", c.TryGetExact("p1", ""), "v1");
            Eq("non-promoted entry evicted", c.TryGetExact("p2", ""), null);
            Eq("newest entry present",   c.TryGetExact("p4", ""), "v4");
        }

        static void Test_CollisionDefense()
        {
            // Store two entries with different (prefix, suffix) pairs.
            // Even if they happened to share a hash (astronomically unlikely), the
            // cache verifies by string equality and returns the correct value for each.
            var c = new CompletionCache();
            c.Store("alpha", "beta",  "result_ab");
            c.Store("gamma", "delta", "result_gd");
            Eq("first entry retrievable",  c.TryGetExact("alpha", "beta"),  "result_ab");
            Eq("second entry retrievable", c.TryGetExact("gamma", "delta"), "result_gd");
            Eq("cross-lookup returns null (wrong prefix)", c.TryGetExact("alpha", "delta"), null);
            Eq("cross-lookup returns null (wrong suffix)", c.TryGetExact("gamma", "beta"),  null);
        }

        static void Test_ExtensionHit()
        {
            var c = new CompletionCache();
            c.Store("foo(", "", "bar)");
            // User typed 'b' — delta="b", remaining="ar)"
            Eq("extension hit", c.TryGetByExtension("foo(b", ""), "ar)");
        }

        static void Test_ExtensionMissWrongChar()
        {
            var c = new CompletionCache();
            c.Store("foo(", "", "bar)");
            // User typed 'x' — completion starts with 'b', not 'x'
            Eq("extension miss wrong char", c.TryGetByExtension("foo(x", ""), null);
        }

        static void Test_ExtensionMissDeltaTooLong()
        {
            var c = new CompletionCache();
            // Completion is long enough to start with a 17-char delta, but delta > 16
            c.Store("foo(", "", "abcdefghijklmnopqrstuvwxyz)");
            // Delta = "abcdefghijklmnopq" (17 chars) — over the 16-char cap
            Eq("extension miss delta too long",
                c.TryGetByExtension("foo(abcdefghijklmnopq", ""), null);
            // Delta = "abcdefghijklmnop" (16 chars) — right at the cap, should hit
            Eq("extension hit at delta cap (16 chars)",
                c.TryGetByExtension("foo(abcdefghijklmnop", ""),
                "qrstuvwxyz)");
        }

        static void Test_ExtensionMissSuffixMismatch()
        {
            var c = new CompletionCache();
            c.Store("foo(", ");", "bar");
            // Suffix is completely unrelated — no match
            Eq("extension miss suffix mismatch",
                c.TryGetByExtension("foo(b", "different);"), null);
        }

        static void Test_ExtensionSuffixIsSubsuffix()
        {
            // cached_suffix.EndsWith(currentSuffix) → match
            var c = new CompletionCache();
            c.Store("foo(", ");\n}", "bar");
            // currentSuffix is a trailing portion of cached suffix
            Eq("extension hit when current suffix is tail of cached suffix",
                c.TryGetByExtension("foo(b", "}"), "ar");
            // exact match also works
            Eq("extension hit exact suffix",
                c.TryGetByExtension("foo(b", ");\n}"), "ar");
        }

        static void Test_Clear()
        {
            var c = new CompletionCache();
            c.Store("p", "s", "v");
            c.Clear();
            Eq("cache empty after Clear", c.TryGetExact("p", "s"), null);
        }

        static void Test_DuplicateStoreUpdatesNotDuplicates()
        {
            var c = new CompletionCache(capacity: 5);
            c.Store("p", "s", "v1");
            c.Store("p", "s", "v2"); // update, not insert
            Eq("updated value returned", c.TryGetExact("p", "s"), "v2");
            // Fill remaining slots to confirm only 1 slot was used for ("p","s")
            c.Store("a", "", "x");
            c.Store("b", "", "x");
            c.Store("c", "", "x");
            c.Store("d", "", "x"); // 5th unique entry — should evict the oldest non-"p" entry
            Eq("original key still present after 4 more stores",
                c.TryGetExact("p", "s"), "v2");
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        static void Eq(string name, string actual, string expected)
        {
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

        static string Fmt(string s) => s == null ? "<null>" : $"\"{s}\"";
    }
}
#endif
