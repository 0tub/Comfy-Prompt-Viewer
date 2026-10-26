using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ComfyPromptViewer;

public struct SearchTerm
{
    public string Text { get; init; }
    public string NormalizedText { get; init; }
    public bool IsExact { get; init; }
}

public static class SearchEngine
{
    private static readonly Regex SearchQueryRegex = new(@"(-)?(?:""([^""]*)""|([^""\s,;]+))", RegexOptions.Compiled);

    public static void ParseQuery(string query, out List<SearchTerm> positiveTerms, out List<SearchTerm> negativeTerms)
    {
        positiveTerms = new List<SearchTerm>();
        negativeTerms = new List<SearchTerm>();

        if (string.IsNullOrWhiteSpace(query))
            return;

        foreach (Match match in SearchQueryRegex.Matches(query))
        {
            bool isNegative = match.Groups[1].Success;
            bool isExact = match.Groups[2].Success;
            string text = isExact ? match.Groups[2].Value : match.Groups[3].Value;

            if (!string.IsNullOrEmpty(text) && text != "-")
            {
                var term = new SearchTerm
                {
                    Text = text,
                    NormalizedText = NormalizeSeparators(text),
                    IsExact = isExact
                };
                if (isNegative)
                    negativeTerms.Add(term);
                else
                    positiveTerms.Add(term);
            }
        }
    }

    public static bool IsMatch(string text, SearchTerm term)
    {
        return term.IsExact
            ? IsExactMatch(text, term.Text)
            : text.Contains(term.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactMatch(string text, string term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            return false;

        var startsWithWord = IsWordChar(term[0]);
        var endsWithWord = IsWordChar(term[term.Length - 1]);
        var index = 0;

        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var hasStartBoundary = !startsWithWord || index == 0 || !IsWordChar(text[index - 1]);
            var endIndex = index + term.Length;
            var hasEndBoundary = !endsWithWord || endIndex == text.Length || !IsWordChar(text[endIndex]);
            if (hasStartBoundary && hasEndBoundary)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    // Character ranges of text that the given terms match, merged and in order, for highlighting. Uses the
    // same rules as IsMatch, so a highlight never disagrees with why the row matched.
    public static List<(int Start, int Length)> FindMatchRanges(string text, IReadOnlyList<SearchTerm> terms)
    {
        var ranges = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text))
        {
            return ranges;
        }

        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term.Text))
            {
                continue;
            }

            var startsWithWord = IsWordChar(term.Text[0]);
            var endsWithWord = IsWordChar(term.Text[^1]);
            var index = 0;
            while ((index = text.IndexOf(term.Text, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var endIndex = index + term.Text.Length;
                if (!term.IsExact ||
                    ((!startsWithWord || index == 0 || !IsWordChar(text[index - 1])) &&
                     (!endsWithWord || endIndex == text.Length || !IsWordChar(text[endIndex]))))
                {
                    ranges.Add((index, term.Text.Length));
                }

                index++;
            }
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(int Start, int Length)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                var end = Math.Max(last.Start + last.Length, range.Start + range.Length);
                merged[^1] = (last.Start, end - last.Start);
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    // Adds a clicked value to what is already typed, so clicks narrow the search. A term already present is
    // not added twice.
    public static string AppendQueryTerm(string currentQuery, string term)
    {
        var current = currentQuery.TrimEnd();
        if (current.Length == 0)
        {
            return term;
        }

        ParseQuery(current, out var existing, out _);
        ParseQuery(term, out var added, out _);
        foreach (var addedTerm in added)
        {
            if (!existing.Exists(existingTerm =>
                    existingTerm.IsExact == addedTerm.IsExact &&
                    string.Equals(existingTerm.Text, addedTerm.Text, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{current} {term}";
            }
        }

        return current;
    }

    // Query text that finds other images sharing a sidebar value. Whole values (model, sampler, seed) are
    // exact terms, so "euler" does not also match "euler_ancestral". A LoRA name is a plain term: its
    // stored form is followed by " (1.00)", which normalizes to "_(1.00)" and would fail an exact term's
    // word boundary. Whitespace becomes '_' so the name stays one term, and the settings column is
    // separator-normalized, so it still matches there. Returns null when nothing searchable is left.
    public static string? BuildValueQuery(string value, bool exact)
    {
        var cleaned = value.Replace("\"", "", StringComparison.Ordinal).Trim();
        if (exact)
        {
            return cleaned.Length == 0 ? null : $"\"{cleaned}\"";
        }

        var builder = new System.Text.StringBuilder(cleaned.Length);
        foreach (var character in cleaned)
        {
            builder.Append(char.IsWhiteSpace(character) ? '_' : character);
        }

        // A leading '-' would read as an exclusion.
        var term = builder.ToString().TrimStart('-');
        return term.Length == 0 ? null : term;
    }

    private static bool IsWordChar(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    internal static string NormalizeSeparators(string value)
    {
        return value.Replace('-', '_').Replace(' ', '_');
    }
}
