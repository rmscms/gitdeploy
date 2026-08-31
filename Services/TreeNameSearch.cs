using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace GitDeployPro.Services
{
    /// <summary>Type-to-search helpers for FTP and Direct Upload trees.</summary>
    public static class TreeNameSearch
    {
        public static readonly DependencyProperty SearchActiveProperty =
            DependencyProperty.RegisterAttached(
                "SearchActive",
                typeof(bool),
                typeof(TreeNameSearch),
                new PropertyMetadata(false));

        public static bool GetSearchActive(DependencyObject obj) =>
            obj != null && (bool)obj.GetValue(SearchActiveProperty);

        public static void SetSearchActive(DependencyObject obj, bool value) =>
            obj?.SetValue(SearchActiveProperty, value);

        public readonly record struct NameParts(string Prefix, string Match, string Suffix);

        public static bool IsTypedSearchText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var ch in text)
            {
                if (!char.IsControl(ch))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool ShouldIgnoreTypedSearch(TextCompositionEventArgs e)
        {
            if (e == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                return true;
            }

            return !IsTypedSearchText(e.Text);
        }

        public static bool Matches(string? name, string? query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return !string.IsNullOrEmpty(name)
                   && name.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        public static NameParts Split(string? name, string? query)
        {
            name ??= string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                return new NameParts(name, string.Empty, string.Empty);
            }

            var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return new NameParts(name, string.Empty, string.Empty);
            }

            return new NameParts(
                name[..index],
                name.Substring(index, query.Length),
                name[(index + query.Length)..]);
        }

        public static List<T> CollectMatches<T>(
            IEnumerable<T> roots,
            string? query,
            Func<T, string> getName,
            Func<T, IEnumerable<T>> getChildren)
        {
            var hits = new List<T>();
            if (string.IsNullOrEmpty(query) || roots == null)
            {
                return hits;
            }

            CollectMatchesCore(roots, query, getName, getChildren, hits);
            return hits;
        }

        private static void CollectMatchesCore<T>(
            IEnumerable<T> nodes,
            string query,
            Func<T, string> getName,
            Func<T, IEnumerable<T>> getChildren,
            List<T> hits)
        {
            foreach (var node in nodes)
            {
                if (Matches(getName(node), query))
                {
                    hits.Add(node);
                }

                CollectMatchesCore(getChildren(node), query, getName, getChildren, hits);
            }
        }

        /// <summary>
        /// Walks the tree, keeps ancestors of matches visible, and reports highlight parts.
        /// Returns true when any node stays visible.
        /// </summary>
        public static bool Apply<T>(
            IEnumerable<T> roots,
            string? query,
            Func<T, string> getName,
            Func<T, IEnumerable<T>> getChildren,
            Action<T, bool, NameParts, bool> apply)
        {
            var anyVisible = false;
            foreach (var root in roots)
            {
                anyVisible |= ApplyNode(root, query, getName, getChildren, apply);
            }

            return anyVisible;
        }

        private static bool ApplyNode<T>(
            T node,
            string? query,
            Func<T, string> getName,
            Func<T, IEnumerable<T>> getChildren,
            Action<T, bool, NameParts, bool> apply)
        {
            var name = getName(node) ?? string.Empty;
            var nameMatches = !string.IsNullOrEmpty(query) && Matches(name, query);
            var childVisible = false;
            foreach (var child in getChildren(node))
            {
                childVisible |= ApplyNode(child, query, getName, getChildren, apply);
            }

            var visible = string.IsNullOrEmpty(query) || nameMatches || childVisible;
            var parts = nameMatches ? Split(name, query) : new NameParts(name, string.Empty, string.Empty);
            apply(node, visible, parts, childVisible && !string.IsNullOrEmpty(query));
            return visible;
        }
    }
}
