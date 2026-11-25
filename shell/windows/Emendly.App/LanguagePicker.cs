using System;
using System.Windows.Controls;

namespace Emendly.App;

internal static class LanguagePicker
{
    // Blank reverts; typed custom or changed text wins; otherwise use the highlighted item.
    public static string? Resolve(ComboBox combo, string? openedValue)
    {
        var typed = combo.Text?.Trim();
        if (string.IsNullOrEmpty(typed))
        {
            return null;
        }

        if (!IsKnown(combo, typed)
            || !string.Equals(typed, openedValue?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return typed;
        }

        return HighlightedItem(combo) ?? typed;
    }

    private static bool IsKnown(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            if (item is string language
                && string.Equals(language, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? HighlightedItem(ComboBox combo)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i)
                is ComboBoxItem { IsHighlighted: true } item)
            {
                return item.Content as string;
            }
        }

        return null;
    }
}
