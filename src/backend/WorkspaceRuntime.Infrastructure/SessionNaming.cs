namespace WorkspaceRuntime.Infrastructure;

// What a session is called. Its own file because it is the kind of one-liner that
// looks obviously right and was not.
public static class SessionNaming
{
    // podman object names have to stay short and the readable prefix is only there
    // so a person reading `podman ps` can tell whose session it is.
    private const int Budget = 40;
    private const int RandomChars = 8;

    // Keep the randomness and truncate the NAME, never the other way round.
    //
    // This used to be:
    //
    //     $"{owner}-{Guid.NewGuid():N}"[..Math.Min(owner.Length + 9, 40)]
    //
    // which spends the budget on the owner first and gives the remainder to the
    // random part. At an owner length of 31 that is still 8 hex digits; at 35 it is
    // 4; at 39 it is NONE — so every session that owner ever opened got the SAME
    // id, and the second `podman --name` collided with the first.
    //
    // Nothing reads the owner back out of an id (the owner comes from the
    // `lunos.owner` label on the container), so the prefix is a convenience and may
    // be cut. The entropy may not.
    //
    // Latent until now, because slugs were short. Organizations compose the
    // organization into the slug, which is exactly what turns a thirty-character
    // owner from improbable into ordinary — which is why this is a prerequisite of
    // that work and not a footnote in it.
    public static string NewId(string owner)
    {
        var stem = owner.Length > Budget - RandomChars - 1
            ? owner[..(Budget - RandomChars - 1)]
            : owner;
        return $"{stem}-{Guid.NewGuid():N}"[..(stem.Length + 1 + RandomChars)];
    }
}
