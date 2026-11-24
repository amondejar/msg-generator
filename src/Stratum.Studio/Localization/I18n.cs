using System.Globalization;
using Microsoft.AspNetCore.Components;
using Stratum.Studio.State;

namespace Stratum.Studio.Localization;

/// <summary>
/// The Studio's language (Spanish or English). Texts are written in both languages where they are used:
/// <c>L("Guardar", "Save")</c>, so nothing can get out of step with a separate resource file. The choice is the
/// user's (remembered in studio.json) and global to the Studio: changing it re-renders every open page.
/// </summary>
public static class I18n
{
    public const string Spanish = "es";
    public const string English = "en";

    private static string? current;

    /// <summary>Raised after the language changes (pages re-render).</summary>
    public static event Action? Changed;

    /// <summary>"es" or "en": the saved choice, else the system's language (Spanish if it is Spanish, else English).</summary>
    public static string Current
    {
        get
        {
            if (current is null)
            {
                // STRATUM_STUDIO_LANGUAGE (es/en) wins: scripts and tests get a known language whatever the user chose.
                var forced = Environment.GetEnvironmentVariable("STRATUM_STUDIO_LANGUAGE");
                var saved = forced is Spanish or English ? forced : StudioSettings.Get("language");
                current = saved is Spanish or English ? saved
                    : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Spanish ? Spanish : English;
            }

            return current;
        }
    }

    public static bool IsEnglish => Current == English;

    public static void Set(string language)
    {
        if (language is not (Spanish or English) || language == Current)
        {
            return;
        }

        current = language;
        StudioSettings.Set("language", language);
        Changed?.Invoke();
    }

    /// <summary>The text in the current language.</summary>
    public static string L(string es, string en) => IsEnglish ? en : es;

    /// <summary>A piece of markup in the current language.</summary>
    public static RenderFragment L(RenderFragment es, RenderFragment en) => IsEnglish ? en : es;
}

/// <summary>Base of the Studio's components: <c>L(es, en)</c> in markup and code.</summary>
public abstract class StudioComponent : ComponentBase
{
    protected static bool En => I18n.IsEnglish;

    protected static string L(string es, string en) => I18n.L(es, en);

    protected static RenderFragment L(RenderFragment es, RenderFragment en) => I18n.L(es, en);
}

/// <summary>Base of the Studio's layout, with the same helpers.</summary>
public abstract class StudioLayoutComponent : LayoutComponentBase
{
    protected static bool En => I18n.IsEnglish;

    protected static string L(string es, string en) => I18n.L(es, en);

    protected static RenderFragment L(RenderFragment es, RenderFragment en) => I18n.L(es, en);
}
