using System;

namespace OllamaCodeCompletions
{
    public static class CompletionPostProcessor
    {
        /// <summary>
        /// Cleans a raw FIM completion before display as ghost text.
        /// Returns null if the completion should be discarded entirely.
        /// </summary>
        public static string Clean(
            string rawCompletion,
            string lineBeforeCursor,
            string lineAfterCursor,
            string bufferAfterCursor)
        {
            // 1. Null / whitespace rejection
            if (string.IsNullOrWhiteSpace(rawCompletion))
                return null;

            string c = rawCompletion;

            // 2. Strip leading newlines when the cursor is already on a fresh/indented line.
            // The model sometimes emits "\n\n" thinking it needs to break first, but the user
            // is already at the start of a new line.
            if (IsAllWhitespace(lineBeforeCursor))
            {
                int start = 0;
                while (start < c.Length && (c[start] == '\r' || c[start] == '\n'))
                    start++;
                if (start > 0)
                    c = c.Substring(start);
            }

            // 3. Cursor is inside an existing line → keep only the first line of the completion.
            if (HasNonWhitespace(lineAfterCursor))
            {
                int nl = IndexOfNewline(c);
                if (nl >= 0)
                    c = c.Substring(0, nl);
            }

            // 4. The model sometimes re-emits text that already follows the cursor.
            // Find the longest suffix of the completion that is a prefix of the buffer
            // and remove it (ignoring purely-whitespace overlaps).
            c = StripSuffixOverlap(c, bufferAfterCursor);

            // 5. Truncate before any closing bracket whose depth would go below zero,
            // skipping characters inside string literals and comments.
            c = TruncateOnUnbalancedBracket(c);

            // 6. Strip echoed prefix — the model sometimes repeats the last few characters
            // that are already present in lineBeforeCursor.
            c = StripPrefixEcho(c, lineBeforeCursor);

            // 7. Re-check: trimming may have left only whitespace.
            if (string.IsNullOrWhiteSpace(c))
                return null;

            return c;
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static bool IsAllWhitespace(string s)
        {
            if (s == null) return true;
            foreach (char ch in s)
                if (!char.IsWhiteSpace(ch)) return false;
            return true;
        }

        private static bool HasNonWhitespace(string s)
        {
            if (s == null) return false;
            foreach (char ch in s)
                if (!char.IsWhiteSpace(ch)) return true;
            return false;
        }

        private static int IndexOfNewline(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\r' || s[i] == '\n') return i;
            return -1;
        }

        // ── filter 4: suffix overlap ──────────────────────────────────────────────

        private static string StripSuffixOverlap(string completion, string bufferAfterCursor)
        {
            if (string.IsNullOrEmpty(completion) || string.IsNullOrEmpty(bufferAfterCursor))
                return completion;

            // Normalize line endings so CRLF vs LF differences at the join boundary don't
            // prevent a valid overlap from being detected.
            string normC = completion.Replace("\r\n", "\n").Replace("\r", "\n");
            string normB = bufferAfterCursor.Replace("\r\n", "\n").Replace("\r", "\n");

            int maxL = Math.Min(normC.Length, normB.Length);
            for (int L = maxL; L >= 1; L--)
            {
                string cSuffix = normC.Substring(normC.Length - L);
                string bPrefix = normB.Substring(0, L);

                if (!string.Equals(cSuffix, bPrefix, StringComparison.Ordinal))
                    continue;

                // A whitespace-only overlap (e.g. a trailing newline) is likely coincidental.
                if (string.IsNullOrWhiteSpace(cSuffix))
                    continue;

                // Map the normalized truncation position back to the original string
                // (the two may differ in length because \r\n collapses to \n).
                int origTruncPos = MapNormToOrig(completion, normC.Length - L);
                return completion.Substring(0, origTruncPos);
            }

            return completion;
        }

        // Maps a character position in the normalized (LF-only) form of `original` back to
        // the corresponding position in the original string.
        private static int MapNormToOrig(string original, int normPos)
        {
            int norm = 0, orig = 0;
            while (orig < original.Length && norm < normPos)
            {
                if (original[orig] == '\r' && orig + 1 < original.Length && original[orig + 1] == '\n')
                {
                    orig += 2; // \r\n counts as one normalized character
                    norm += 1;
                }
                else
                {
                    orig++;
                    norm++;
                }
            }
            return orig;
        }

        // ── filter 5: bracket balance ─────────────────────────────────────────────

        private static string TruncateOnUnbalancedBracket(string completion)
        {
            if (string.IsNullOrEmpty(completion))
                return completion;

            int parenDepth = 0, squareDepth = 0, braceDepth = 0;
            bool inDouble = false, inSingle = false, inLineComment = false, inBlockComment = false;

            for (int i = 0; i < completion.Length; i++)
            {
                char ch = completion[i];
                char next = i + 1 < completion.Length ? completion[i + 1] : '\0';

                if (inLineComment)
                {
                    if (ch == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (ch == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inDouble)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == '"') inDouble = false;
                    continue;
                }
                if (inSingle)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == '\'') inSingle = false;
                    continue;
                }

                if (ch == '/' && next == '/') { inLineComment = true; i++; continue; }
                if (ch == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (ch == '"') { inDouble = true; continue; }
                if (ch == '\'') { inSingle = true; continue; }

                if      (ch == '(') parenDepth++;
                else if (ch == ')') { if (--parenDepth  < 0) return completion.Substring(0, i); }
                else if (ch == '[') squareDepth++;
                else if (ch == ']') { if (--squareDepth < 0) return completion.Substring(0, i); }
                else if (ch == '{') braceDepth++;
                else if (ch == '}') { if (--braceDepth  < 0) return completion.Substring(0, i); }
            }

            return completion;
        }

        // ── filter 6: prefix echo ─────────────────────────────────────────────────

        private static string StripPrefixEcho(string completion, string lineBeforeCursor)
        {
            if (string.IsNullOrEmpty(completion) || string.IsNullOrEmpty(lineBeforeCursor))
                return completion;

            // Check whether the completion starts with the last N characters of the line.
            // Cap at 40 to avoid pathological matches; require at least 2 to avoid false positives.
            int checkLen = Math.Min(lineBeforeCursor.Length, 40);
            for (int n = checkLen; n >= 2; n--)
            {
                string tail = lineBeforeCursor.Substring(lineBeforeCursor.Length - n);
                if (completion.StartsWith(tail, StringComparison.Ordinal))
                    return completion.Substring(n);
            }

            return completion;
        }
    }
}
