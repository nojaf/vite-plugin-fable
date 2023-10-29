import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fable from "../index.js";
import { fileURLToPath } from "node:url";
import path from "node:path";
const repositoryRoot = path.dirname(fileURLToPath(import.meta.url));

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react(), fable({
    fsproj: path.join(repositoryRoot, "App.fsproj")
  })],
})
