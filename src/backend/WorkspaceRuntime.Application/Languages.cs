namespace WorkspaceRuntime.Application;

// What language a person works in, and what that means for their desk.
//
// Three, because those are the three the product is being built for; the shape
// takes more without changing, but claiming to support a language nobody has read
// the translations for would be worse than not offering it.
public sealed record UiLanguage(
    string Code,
    string EnglishName,
    string NativeName,
    // The locale the session runs under. Getting this wrong is not cosmetic: a
    // desk on C.UTF-8 sorts and formats wrongly and mangles anything outside
    // ASCII, which people describe as "it looked fine until I typed my own name".
    string Locale,
    // Keyboard layouts, in order. `us` is always present and always last-resort:
    // ASCII paths, shell commands and the agent's own keysyms have to keep
    // working whatever the person writes in.
    string Layouts);

public static class Languages
{
    public static readonly UiLanguage English = new("en", "English", "English", "en_US.UTF-8", "us");
    public static readonly UiLanguage Russian = new("ru", "Russian", "Русский", "ru_RU.UTF-8", "us,ru");
    public static readonly UiLanguage Spanish = new("es", "Spanish", "Español", "es_ES.UTF-8", "us,es");

    public static IReadOnlyList<UiLanguage> All { get; } = new[] { English, Russian, Spanish };

    public const string Default = "en";

    // Unknown resolves to English rather than failing. A language removed in a
    // later release, or a code from a newer panel, must still open a desk — the
    // person gets an English interface, which is inconvenient, rather than no
    // machine, which is not.
    public static UiLanguage Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return English;
        }

        // Match the primary subtag, so "ru-RU" and "es-419" land where they mean
        // to instead of falling through to English on a regional variant.
        var primary = code.Split('-', '_')[0];
        return All.FirstOrDefault(language =>
                   string.Equals(language.Code, primary, StringComparison.OrdinalIgnoreCase))
               ?? English;
    }

    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && All.Any(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));
}
