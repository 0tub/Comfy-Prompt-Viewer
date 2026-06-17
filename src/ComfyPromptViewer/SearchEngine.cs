using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ComfyPromptViewer;

public struct SearchTerm
{
    public string Text { get; init; }
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
                var term = new SearchTerm { Text = text, IsExact = isExact };
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

    public static bool IsMatch(string firstText, string secondText, SearchTerm term)
    {
        return IsMatch(firstText, term) || IsMatch(secondText, term);
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

    private static bool IsWordChar(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }
}
