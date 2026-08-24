# CieloOS marketing site

A static Vite + TypeScript site with a lightweight three.js hero. The site is self-contained apart from optional Google Fonts; system fonts take over if they cannot load.

## Run locally

```bash
npm install
npm run dev
```

## Build

```bash
npm run build
```

The static output is written to `dist/`. Vite uses a relative asset base, so the directory can be served from a GitHub Pages project path without rewriting asset URLs.

## Deploy to GitHub Pages

Build the project, then publish the contents of `dist/` with a Pages workflow or a `gh-pages` branch. For a workflow, run the build from the `site/` working directory and upload `site/dist` with `actions/upload-pages-artifact`.

## Rendering behavior

The HTML and CSS paint the complete first view before three.js loads. The WebGL scene initializes only when the hero enters the viewport. If WebGL creation fails, a designed CSS representation of the same human/agent command flow remains visible. With `prefers-reduced-motion: reduce`, WebGL does not initialize and the still composition is used intentionally.
