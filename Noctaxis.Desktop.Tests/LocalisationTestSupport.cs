using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Test-only stand-in for a translation: every resolved text becomes about 35% longer, as German or
/// French typically are, with its placeholders untouched. Anything built inside the scope (a window,
/// a view model's text) uses the longer text; nothing outside the calling test is affected.
/// </summary>
internal static partial class PseudoLocalisation
{
    public const double Growth = .35;

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    public static IDisposable Apply()
    {
        Strings.Transform = (_, text) => Expand(text);
        return new Scope();
    }

    public static string Expand(string text)
    {
        var letters = Placeholder().Replace(text, string.Empty).Count(char.IsLetter);
        if (letters == 0) return text;
        var extra = (int)Math.Ceiling(letters * Growth);
        var filler = new StringBuilder();
        // Prose grows by extra words, which can wrap; a single word just grows.
        var prose = text.Trim().Contains(' ');
        for (var added = 0; added < extra;)
        {
            var word = Math.Min(5, extra - added);
            if (prose) filler.Append(' ');
            filler.Append('x', word);
            added += word;
        }
        var trailing = text.Length - text.TrimEnd().Length;
        return text[..^trailing] + filler + text[^trailing..];
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Strings.Transform = null;
    }
}

internal static class TestCulture
{
    /// <summary>Runs with another culture for formatting and for resource lookup, then restores both.</summary>
    public static T In<T>(string name, Func<T> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }

    /// <summary>
    /// Runs with another regional format only, as a user with that Windows region sees Noctaxis: the
    /// text stays in the application language (English); numbers, dates and times follow the region.
    /// </summary>
    public static T InRegion<T>(string name, Func<T> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(AppLanguages.DefaultId);
            return action();
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }
}

/// <summary>
/// Finds text that does not fit: wider or taller than its own box, cut off with an ellipsis, or
/// running past a clipping ancestor or the window. It looks at structure, not pixels.
/// </summary>
internal static class LayoutAudit
{
    public sealed record Problem(int Index, string Kind, string Text);

    public static IReadOnlyList<Problem> Problems(Visual root)
    {
        var problems = new List<Problem>();
        var index = -1;
        var rootBounds = new Rect(root.Bounds.Size);
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            index++;
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(text.Text) || text.Bounds.Width <= 0) continue;
            // Symbols (the ×, − and □ glyph buttons) are not translated and do not grow.
            if (!text.Text!.Any(char.IsLetter)) continue;
            var layout = text.TextLayout;
            var shown = text.Text!.Length > 40 ? text.Text[..40] + "…" : text.Text;
            if (layout.TextLines.Any(line => line.HasCollapsed)) problems.Add(new(index, "trimmed", shown));
            var box = text.Bounds.Size.Deflate(text.Padding);
            if (layout.Width > box.Width + 1) problems.Add(new(index, "wider than its box", shown));
            if (layout.Height > box.Height + 1) problems.Add(new(index, "taller than its box", shown));
            if (text.TranslatePoint(default, root) is not { } origin) continue;
            var left = origin.X;
            var right = origin.X + text.Bounds.Width;
            var scrollsHorizontally = false;
            foreach (var ancestor in text.GetVisualAncestors())
            {
                if (ancestor is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled }) scrollsHorizontally = true;
                if (scrollsHorizontally) break;
                if (ancestor is not Control { ClipToBounds: true } clip || clip.TranslatePoint(default, root) is not { } clipOrigin) continue;
                if (left < clipOrigin.X - 1 || right > clipOrigin.X + clip.Bounds.Width + 1)
                {
                    problems.Add(new(index, $"cut off by {clip.GetType().Name}", shown));
                    break;
                }
            }
            if (!scrollsHorizontally && (left < -1 || right > rootBounds.Width + 1)) problems.Add(new(index, "outside the window", shown));
        }
        return problems;
    }
}
