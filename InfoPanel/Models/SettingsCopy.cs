using System;
using System.Collections.Generic;
using System.Reflection;

namespace InfoPanel.Models;

/// <summary>
/// Copies persisted settings onto the live singleton.
/// Every public read/write property is copied, so a new setting is not silently
/// reset on the next start because someone forgot to add a line in LoadSettings.
/// Get-only collections are not properties this can assign; the caller replaces those.
/// </summary>
public static class SettingsCopy
{
    public static void CopySettableProperties(object target, object source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (target.GetType() != source.GetType())
            throw new ArgumentException("Settings copy requires two instances of the same type.", nameof(source));

        foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
                continue;
            if (property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
                continue;

            property.SetValue(target, property.GetValue(source));
        }
    }

    public static void ReplaceItems<T>(IList<T> target, IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
