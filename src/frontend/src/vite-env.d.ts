/// <reference types="vite/client" />

// Without this, `import.meta.env` is a type error and `npm run build` fails at
// `tsc -b` before Vite ever runs. shared/i18n.tsx reads import.meta.env.DEV to
// warn about a missing translation only in development; the reference lives in
// its own file rather than in tsconfig's `types`, so adding a second ambient
// declaration later does not mean editing compiler options again.
