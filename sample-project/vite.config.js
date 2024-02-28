import { defineConfig } from "vite";
import Inspect from "vite-plugin-inspect";
import fable from "../index.js";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repositoryRoot = path.dirname(fileURLToPath(import.meta.url));

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [Inspect(), fable()],
});
