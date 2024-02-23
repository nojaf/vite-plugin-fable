import { defineConfig } from "vite";
import Inspect from "vite-plugin-inspect";
import react from "@vitejs/plugin-react";
import fable from "../index.js";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repositoryRoot = path.dirname(fileURLToPath(import.meta.url));

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [Inspect(), fable()],
  server: {
    //https://github.com/vitejs/vite/issues/15784
    fs: {
      cachedChecks: false,
    },
  },
});
