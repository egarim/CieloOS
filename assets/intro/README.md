# CieloOS intro animation

A WebGL splash that resolves into the CieloOS wordmark. Parked here for later use —
**nothing in the product references it yet.** Open either file directly in a browser.

| File | What |
|---|---|
| `cieloos-intro.html` | the intro |
| `cieloos-intro-fire.html` | a variant with a fire/ember treatment |
| `three.min.js` | vendored three.js (MIT, © 2010-2023 three.js authors) |

## Notes for whoever picks this up

- **three.js is vendored, not fetched.** The page loads `three.min.js` from this
  directory, so the animation works offline and in a session with no egress — which is
  the right posture for something that might front a boot or first-run screen.
- **It is the deprecated non-module build.** three.js prints a console warning that
  `build/three.min.js` is deprecated from r150 and suggests ES modules. Fine for a
  static splash; worth migrating if this becomes a real surface.
- **The no-WebGL fallback points at a file that does not exist.** Both pages say to use
  a canvas-2D version if WebGL is unavailable, but no such file was provided. Either
  write it or change the message before shipping this anywhere a user might see it.
- **Untested in a session.** It has not been run inside a desktop session (browser
  streaming plus WebGL is exactly where this could disappoint) or against the panel.

## Possible uses

First-run/claim screen, a boot splash for the kiosk mode, or the desktop session's
initial screen. All of those imply the WebGL fallback question above is answered first.
