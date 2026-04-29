using System;
using System.Collections.Generic;

namespace OllamaCopilot
{
    /// <summary>
    /// Bounded LRU cache of post-processed completions, keyed by (prefix, suffix).
    /// One instance lives per <see cref="SuggestionSession"/> (per editor view).
    /// All public methods are thread-safe — Store may be called from a thread-pool
    /// thread while TryGet* is called from the UI thread.
    /// </summary>
    internal sealed class CompletionCache
    {
        private sealed class Entry
        {
            public long   Key;
            public string Prefix;
            public string Suffix;
            public string Completion;
        }

        private readonly int _capacity;
        private readonly Dictionary<long, LinkedListNode<Entry>> _map;
        private readonly LinkedList<Entry> _lru; // head = most recently used
        private readonly object _lock = new object();

        public CompletionCache(int capacity = 100)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<long, LinkedListNode<Entry>>(_capacity + 1);
            _lru = new LinkedList<Entry>();
        }

        // ── public API ────────────────────────────────────────────────────────────

        /// <summary>Returns the cached completion for the exact (prefix, suffix) pair, or null.</summary>
        public string TryGetExact(string prefix, string suffix)
        {
            long key = HashKey(prefix, suffix);
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out LinkedListNode<Entry> node))
                    return null;

                Entry e = node.Value;
                if (e.Prefix != prefix || e.Suffix != suffix)
                    return null; // hash collision — treat as miss

                MoveToFront(node);
                return e.Completion;
            }
        }

        /// <summary>
        /// Looks for a cached entry whose prefix is a strict prefix of <paramref name="prefix"/>
        /// and whose completion starts with the extra characters the user just typed.
        /// Returns the remainder of that completion (the part not yet typed), or null.
        /// Walks at most the 20 most-recent entries.
        /// </summary>
        public string TryGetByExtension(string prefix, string suffix)
        {
            lock (_lock)
            {
                int scanned = 0;
                LinkedListNode<Entry> node = _lru.First;
                while (node != null && scanned < 20)
                {
                    Entry e = node.Value;

                    if (prefix.Length > e.Prefix.Length &&
                        prefix.StartsWith(e.Prefix, StringComparison.Ordinal))
                    {
                        string delta = prefix.Substring(e.Prefix.Length);

                        if (delta.Length <= 16 &&
                            e.Completion.StartsWith(delta, StringComparison.Ordinal) &&
                            SuffixMatches(suffix, e.Suffix))
                        {
                            MoveToFront(node);
                            return e.Completion.Substring(delta.Length);
                        }
                    }

                    node = node.Next;
                    scanned++;
                }
                return null;
            }
        }

        /// <summary>
        /// Stores a completion. If the key already exists the completion is updated
        /// and the entry is promoted. Evicts the LRU entry when at capacity.
        /// </summary>
        public void Store(string prefix, string suffix, string completion)
        {
            long key = HashKey(prefix, suffix);
            lock (_lock)
            {
                if (_map.TryGetValue(key, out LinkedListNode<Entry> existing))
                {
                    if (existing.Value.Prefix == prefix && existing.Value.Suffix == suffix)
                    {
                        // Update in-place and promote.
                        existing.Value.Completion = completion;
                        MoveToFront(existing);
                        return;
                    }

                    // Hash collision — evict the colliding entry before inserting.
                    _lru.Remove(existing);
                    _map.Remove(key);
                }

                if (_lru.Count >= _capacity)
                {
                    LinkedListNode<Entry> tail = _lru.Last;
                    _map.Remove(tail.Value.Key);
                    _lru.RemoveLast();
                }

                var entry = new Entry
                {
                    Key        = key,
                    Prefix     = prefix,
                    Suffix     = suffix,
                    Completion = completion,
                };
                var newNode = new LinkedListNode<Entry>(entry);
                _lru.AddFirst(newNode);
                _map[key] = newNode;
            }
        }

        /// <summary>Empties the cache (call when the model changes).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
            }
        }

        // ── internals ─────────────────────────────────────────────────────────────

        private void MoveToFront(LinkedListNode<Entry> node)
        {
            if (_lru.First == node) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        // The suffix of a prefix-extension match is valid when:
        //   • it equals the cached suffix exactly (user typed a new character), OR
        //   • it is a suffix of the cached suffix (user typed into the cached suffix region).
        private static bool SuffixMatches(string currentSuffix, string cachedSuffix)
        {
            return currentSuffix == cachedSuffix ||
                   cachedSuffix.EndsWith(currentSuffix, StringComparison.Ordinal);
        }

        // FNV-1a 64-bit hash of (prefix.Length, prefix chars, suffix chars).
        // Including the prefix length avoids ("a","bc") colliding with ("ab","c").
        internal static long HashKey(string prefix, string suffix)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                hash = (hash ^ (uint)prefix.Length) * 1099511628211UL;
                foreach (char c in prefix)
                    hash = (hash ^ c) * 1099511628211UL;
                foreach (char c in suffix)
                    hash = (hash ^ c) * 1099511628211UL;
                return (long)hash;
            }
        }
    }
}
