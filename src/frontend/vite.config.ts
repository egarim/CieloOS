import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

const __dirname = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": resolve(__dirname, "./src")
    }
  },
  server: {
    port: 5173,
    proxy: {
      "/api": `http://127.0.0.1:${process.env.BACKEND_PORT ?? "5148"}`
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts"
  },
  build: {
    rollupOptions: {
      input: {
        main: resolve(__dirname, "index.html"),
        portal: resolve(__dirname, "portal.html")
      }
    }
  }
});
