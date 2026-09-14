import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// Unmount between tests, here rather than in each file.
//
// React Testing Library only auto-cleans when vitest runs with `globals: true`,
// which this project does not set, so without this every render stays in the
// document and the next test searches a page containing both. That does not only
// produce confusing "found multiple elements" errors — it lets a test PASS on an
// element the previous test rendered, which is the false-pass pattern this repo
// keeps finding in other forms. A file that forgets to call cleanup() should not
// be able to create one.
afterEach(() => {
  cleanup();
});
