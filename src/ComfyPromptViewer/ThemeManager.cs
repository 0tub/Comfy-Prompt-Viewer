using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ComfyPromptViewer;

internal static class ThemeManager
{
    // Each palette is its own ThemeVariant rather than a set of brushes to overwrite. Every variant
    // inherits from Dark, so the Fluent resources this app does not override (scrollbars, checkboxes,
    // text selection, focus adorners) resolve dark instead of following the OS light/dark setting.
    private static readonly Dictionary<ThemeMode, ThemeVariant> ThemeVariants = new()
    {
        [ThemeMode.Brown] = new ThemeVariant(nameof(ThemeMode.Brown), ThemeVariant.Dark),
        [ThemeMode.DarkGray] = new ThemeVariant(nameof(ThemeMode.DarkGray), ThemeVariant.Dark),
        [ThemeMode.DarkBlue] = new ThemeVariant(nameof(ThemeMode.DarkBlue), ThemeVariant.Dark),
        [ThemeMode.DarkGreen] = new ThemeVariant(nameof(ThemeMode.DarkGreen), ThemeVariant.Dark),
        [ThemeMode.Plum] = new ThemeVariant(nameof(ThemeMode.Plum), ThemeVariant.Dark)
    };

    private sealed record ThemePalette(
        string BackgroundBase,
        string SurfaceCard,
        string SurfaceSidebar,
        string SurfaceElevated,
        string SurfaceInput,
        string BorderSubtle,
        string ToolbarBorderSubtle,
        string BorderAccent,
        string CardHoverBorder,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string TextAccent,
        string EmptyStateSubtext);

    // The large-preview backdrop is the base background at partial opacity so the gallery stays faintly
    // visible behind it. Every other themed brush is fully opaque.
    private const string LargePreviewOverlayBackgroundKey = "LargePreviewOverlayBackground";
    private const double LargePreviewOverlayOpacity = 0.85;

    private static readonly (string Key, Func<ThemePalette, string> Color)[] BrushResources =
    [
        ("BackgroundBase", p => p.BackgroundBase),
        ("SurfaceBase", p => p.BackgroundBase),
        ("LargePreviewOverlayBackground", p => p.BackgroundBase),
        ("SurfaceCard", p => p.SurfaceCard),
        ("SurfaceSidebar", p => p.SurfaceSidebar),
        ("SurfaceElevated", p => p.SurfaceElevated),
        ("SurfaceInput", p => p.SurfaceInput),
        ("BorderSubtle", p => p.BorderSubtle),
        ("ToolbarBorderSubtle", p => p.ToolbarBorderSubtle),
        ("BorderAccent", p => p.BorderAccent),
        ("CardHoverBorder", p => p.CardHoverBorder),
        ("TextPrimary", p => p.TextPrimary),
        ("TextSecondary", p => p.TextSecondary),
        ("TextMuted", p => p.TextMuted),
        ("TextAccent", p => p.TextAccent),
        ("PromptText", p => p.TextSecondary),
        ("EmptyStateSubtext", p => p.EmptyStateSubtext),
        ("SystemControlHighlightAccentBrush", p => p.BorderAccent),
        ("AccentFillColorDefaultBrush", p => p.BorderAccent),
        ("AccentFillColorSecondaryBrush", p => p.BorderAccent),
        ("AccentFillColorTertiaryBrush", p => p.BorderAccent),
        ("SliderThumbBackground", p => p.BorderAccent),
        ("SliderThumbBackgroundPointerOver", p => p.TextAccent),
        ("SliderThumbBackgroundPressed", p => p.BorderAccent),
        ("SliderThumbBackgroundDisabled", p => p.BorderSubtle),
        ("SliderTrackFill", p => p.BorderSubtle),
        ("SliderTrackFillPointerOver", p => p.BorderSubtle),
        ("SliderTrackFillPressed", p => p.BorderSubtle),
        ("SliderTrackFillDisabled", p => p.SurfaceInput),
        ("SliderTrackValueFill", p => p.BorderAccent),
        ("SliderTrackValueFillPointerOver", p => p.BorderAccent),
        ("SliderTrackValueFillPressed", p => p.BorderAccent),
        ("SliderTrackValueFillDisabled", p => p.BorderSubtle),
        ("ComboBoxBackground", p => p.SurfaceInput),
        ("ComboBoxBackgroundPointerOver", p => p.SurfaceElevated),
        ("ComboBoxBackgroundPressed", p => p.SurfaceInput),
        ("ComboBoxBackgroundDisabled", p => p.SurfaceSidebar),
        ("ComboBoxBorderBrush", p => p.BorderSubtle),
        ("ComboBoxBorderBrushPointerOver", p => p.BorderAccent),
        ("ComboBoxBorderBrushPressed", p => p.BorderAccent),
        ("ComboBoxBorderBrushDisabled", p => p.BorderSubtle),
        ("ComboBoxDropdownBackground", p => p.SurfaceCard),
        ("ComboBoxDropDownBackground", p => p.SurfaceCard),
        ("ComboBoxDropdownBorderBrush", p => p.BorderSubtle),
        ("ComboBoxDropDownBorderBrush", p => p.BorderSubtle),
        ("ComboBoxForeground", p => p.TextPrimary),
        ("ComboBoxForegroundPointerOver", p => p.TextPrimary),
        ("ComboBoxForegroundPressed", p => p.TextPrimary),
        ("ComboBoxForegroundDisabled", p => p.TextMuted),
        ("ComboBoxItemBackgroundPointerOver", p => p.SurfaceInput),
        ("ComboBoxItemBackgroundPressed", p => p.SurfaceInput),
        ("ComboBoxItemBackgroundSelected", p => p.BorderAccent),
        ("ComboBoxItemBackgroundSelectedPointerOver", p => p.TextAccent),
        ("ComboBoxItemBackgroundSelectedPressed", p => p.BorderAccent),
        ("ComboBoxItemForeground", p => p.TextSecondary),
        ("ComboBoxItemForegroundPointerOver", p => p.TextPrimary),
        ("ComboBoxItemForegroundPressed", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelected", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelectedPointerOver", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelectedPressed", p => p.TextPrimary),
        ("ComboBoxItemForegroundDisabled", p => p.TextMuted),
        ("MenuFlyoutPresenterBackground", p => p.BackgroundBase),
        ("MenuFlyoutPresenterBorderBrush", p => p.BorderSubtle),
        ("MenuFlyoutItemBackgroundPointerOver", p => p.SurfaceCard),
        ("MenuFlyoutItemBackgroundPressed", p => p.SurfaceInput),
        ("MenuFlyoutItemForeground", p => p.TextSecondary),
        ("MenuFlyoutItemForegroundPointerOver", p => p.TextPrimary),
        ("MenuFlyoutItemForegroundPressed", p => p.TextPrimary),
        ("MenuFlyoutItemForegroundDisabled", p => p.TextMuted)
    ];

    // Builds one resource dictionary per palette and registers it as a theme dictionary. Must run before
    // the first Apply, and only once: rebuilding would hand out new brush instances to a live visual tree.
    internal static void Initialize()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var (themeMode, variant) in ThemeVariants)
        {
            if (resources.ThemeDictionaries.ContainsKey(variant))
            {
                continue;
            }

            var palette = GetPalette(themeMode);
            var dictionary = new ResourceDictionary
            {
                ["SystemAccentColor"] = Color.Parse(palette.BorderAccent)
            };

            foreach (var (key, color) in BrushResources)
            {
                dictionary[key] = new SolidColorBrush(Color.Parse(color(palette)))
                {
                    Opacity = key == LargePreviewOverlayBackgroundKey ? LargePreviewOverlayOpacity : 1.0
                };
            }

            resources.ThemeDictionaries[variant] = dictionary;
        }
    }

    // Switching the palette is one property assignment; the resource system re-resolves every
    // DynamicResource against the new variant.
    internal static void Apply(ThemeMode themeMode)
    {
        if (Application.Current is { } application && ThemeVariants.TryGetValue(themeMode, out var variant))
        {
            application.RequestedThemeVariant = variant;
        }
    }

    private static ThemePalette GetPalette(ThemeMode themeMode)
    {
        return themeMode switch
        {
            ThemeMode.DarkGray => new ThemePalette(
                "#111315", "#202326", "#171A1D", "#1D2023", "#24282C", "#343A40", "#434B52",
                "#6E7681", "#464D55", "#F1F4F6", "#CFD6DD", "#9AA4AE", "#A8B2BD", "#9AA4AE"),
            ThemeMode.DarkBlue => new ThemePalette(
                "#0D1320", "#182236", "#111A2B", "#162033", "#1B2940", "#2A3A56", "#394D70",
                "#3D6EA8", "#344A68", "#EEF5FF", "#C7D6EA", "#94A8C1", "#7EA7D8", "#94A8C1"),
            ThemeMode.DarkGreen => new ThemePalette(
                "#0D1712", "#18251D", "#111D16", "#16231B", "#1C2B22", "#2C3E32", "#3B5142",
                "#4F7D5E", "#3A4E40", "#EFF7F0", "#CDDED0", "#9AAE9E", "#83B88E", "#9AAE9E"),
            ThemeMode.Plum => new ThemePalette(
                "#17111B", "#251B2B", "#1C1421", "#231928", "#2B2032", "#3B2D45", "#4D3A5A",
                "#7B4F8C", "#4A3855", "#F5EDF7", "#DDCCE3", "#AD97B6", "#B985C8", "#AD97B6"),
            _ => new ThemePalette(
                "#17120D", "#272016", "#1C1610", "#261E16", "#2C2318", "#3A2E22", "#4A3A2A",
                "#8B3A2E", "#4A3D30", "#F5EDE0", "#D7C4AA", "#B29C83", "#D4795A", "#B29C83")
        };
    }

}
