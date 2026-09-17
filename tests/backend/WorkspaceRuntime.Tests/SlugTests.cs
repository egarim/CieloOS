using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

// A person's name becomes their username, and the username is what they type to
// sign in. Nothing tested this, and it showed: Slug.Of kept only ASCII a-z0-9 and
// collapsed every other run to a dash, so it did not transliterate an accented
// letter — it deleted it and left a dash in the hole.
//
//   "José Ojeda"  ->  "jos-ojeda"
//
// Which the operator then had to type, exactly, to log in. The comment on Slug.Of
// even documented this as intended ("José Peña" -> "jos-pe-a") without noticing
// that it makes the product unusable for anyone whose name is not plain ASCII.
public class SlugTests
{
    [Theory]
    // Plain ASCII is unchanged: whatever else happens, this must not move.
    [InlineData("Ada Lovelace", "ada-lovelace")]
    [InlineData("acme_maria", "acme-maria")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("Robert '); DROP TABLE", "robert-drop-table")]
    [InlineData("MiXeD CaSe 99", "mixed-case-99")]
    // The letter under the accent survives, rather than becoming a dash.
    [InlineData("José Ojeda", "jose-ojeda")]
    [InlineData("José Peña", "jose-pena")]
    [InlineData("Zoë Müller", "zoe-muller")]
    [InlineData("Françoise Aubé", "francoise-aube")]
    [InlineData("Ángel Ruiz", "angel-ruiz")]
    // Latin letters with no canonical decomposition: the accent is part of the
    // glyph, so stripping combining marks is not enough on its own.
    [InlineData("Søren Kierkegaard", "soren-kierkegaard")]
    [InlineData("Łukasz Nowak", "lukasz-nowak")]
    [InlineData("Weiß Straße", "weiss-strasse")]
    public void DerivesAUsernameAPersonCanType(string name, string expected)
    {
        Assert.Equal(expected, Slug.Of(name));
    }

    [Theory]
    // A script we cannot transliterate must come out EMPTY rather than as a
    // string of dashes. Empty is what the caller checks to say "that name will
    // not do, choose a username"; "----" is a username nobody can guess and a
    // collision waiting to happen between two different people.
    [InlineData("Вера Морозова")]
    [InlineData("日本語の名前")]
    [InlineData("!!!")]
    [InlineData("   ")]
    [InlineData("")]
    public void RefusesRatherThanInventingAUsername(string name)
    {
        Assert.Equal("", Slug.Of(name));
    }
}
