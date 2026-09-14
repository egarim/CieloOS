// @vitest-environment node
//
// Node, not jsdom: this test loads the real Vite config, which runs esbuild, and
// esbuild refuses to start when TextEncoder comes from jsdom's realm. Nothing here
// touches a DOM anyway — it is asking the build system a question about itself.

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test } from "vitest";
import { loadConfigFromFile } from "vite";

// The portal is a SECOND build entry. Nothing about the running app tells you it
// is still wired: the admin panel keeps working, every component still imports,
// and the only symptom is that portal.html stops being emitted — which nobody
// notices until an install has no portal to open.
//
// The first version of this test regexed vite.config.ts for the string
// "portal.html". Codex was right that it proved nothing: the same text inside a
// comment, a dead object, or a branch that never executes would have passed it.
// So this loads the real config through Vite itself and asks the resolved value.

const frontendRoot = process.cwd();

test("Vite really resolves a portal entry, not just a mention of one", async () => {
  const loaded = await loadConfigFromFile(
    { command: "build", mode: "production" },
    resolve(frontendRoot, "vite.config.ts"),
    undefined,
    undefined,
    undefined,
    "native",
  );

  expect(loaded, "vite.config.ts did not load at all").not.toBeNull();

  const input = loaded!.config.build?.rollupOptions?.input;
  expect(input, "build.rollupOptions.input is not set").toBeTruthy();
  expect(typeof input, "input should be the named-entries object").toBe("object");

  const entries = input as Record<string, string>;

  // Both entries, because dropping the admin panel while adding the portal is
  // the other way this can go wrong.
  expect(Object.keys(entries).sort()).toEqual(["main", "portal"]);

  // The paths must point at files that exist. An input naming a deleted file is
  // how this breaks in practice, and Vite only complains at build time.
  for (const [name, file] of Object.entries(entries)) {
    expect(existsSync(file), `the ${name} entry points at a file that is not there: ${file}`).toBe(true);
  }

  expect(entries.portal.replace(/\\/g, "/")).toMatch(/\/portal\.html$/);
});

test("portal.html loads the portal entry module, and that module exists", () => {
  const html = readFileSync(resolve(frontendRoot, "portal.html"), "utf8");

  const script = html.match(/<script[^>]+type=["']module["'][^>]+src=["']([^"']+)["']/);
  expect(script, "portal.html has no module script tag").not.toBeNull();

  const src = script![1];
  expect(existsSync(resolve(frontendRoot, src.replace(/^\//, ""))),
    `portal.html points at ${src}, which does not exist`).toBe(true);

  // It must be the portal's entry, not the admin panel's. Pointing both HTML
  // files at /src/main.tsx would build, serve, and be completely wrong.
  expect(src).toMatch(/^\/src\/portal\//);
});
