import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath } from "vite";

const fsharpFileRegex = /\.(fs|fsi)$/;
const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fableDaemon = path.join(
  currentDir,
  "artifacts/bin/Fable.Daemon/release_linux-x64/Fable.Daemon.dll",
);
const dotnetProcess = spawn("dotnet", [fableDaemon, "--stdio"], {
  shell: true,
  stdio: "pipe",
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout);
const fableLibrary = path.join(process.cwd(), "node_modules/fable-library");

async function findFsProjFile(configDir) {
  const files = await fs.readdir(configDir);
  const fsprojFiles = files
    .filter((file) => file && file.toLocaleLowerCase().endsWith(".fsproj"))
    .map((fsProjFile) => {
      // Return the full path of the .fsproj file
      return normalizePath(path.join(configDir, fsProjFile));
    });
  return fsprojFiles.length > 0 ? fsprojFiles[0] : null;
}

async function getProjectFile(project) {
  return await endpoint.send("fable/init", {
    project,
    fableLibrary,
  });
}

export default function fablePlugin(config = {}) {
  // A map of <js filePath, code>
  const compilableFiles = new Map();
  /** @typedef {object} json
   * @property {string[]} sourceFiles
   */
  let projectOptions = null;
  let fsproj;

  return {
    name: "vite-plugin-fable",
    configResolved: async function (resolvedConfig) {
      const configDir = path.dirname(resolvedConfig.configFile);

      if (config && config.fsproj) {
        fsproj = config.fsproj;
      } else {
        fsproj = await findFsProjFile(configDir);
      }

      if (!fsproj) {
        resolvedConfig.logger.error(
          `[configResolved] No .fsproj file was found in ${configDir}`,
        );
      } else {
        resolvedConfig.logger.info(`[configResolved] Entry fsproj ${fsproj}`);
      }
    },
    buildStart: async function (options) {
      this.info(`[buildStart] Initial compile started of ${fsproj}`);
      /** @typedef {object} json
       * @property {string} Case
       * @property {string[]} Fields
       */
      const projectResponse = await getProjectFile(fsproj);
      if (
        projectResponse.Case === "Success" &&
        projectResponse.Fields &&
        projectResponse.Fields.length === 2
      ) {
        this.info(`[buildStart] Initial compile completed of ${fsproj}`);
        projectOptions = projectResponse.Fields[0];
        const compiledFSharpFiles = projectResponse.Fields[1];
        // for proj file
        projectOptions.sourceFiles.forEach((file) => {
          this.addWatchFile(file);
          compilableFiles.set(file, compiledFSharpFiles[file]);
        });
      } else {
        this.warn({
          message: "[buildStart] Unexpected projectResponse",
          meta: {
            projectResponse,
          },
        });
      }
    },
    transform(src, id) {
      if (fsharpFileRegex.test(id)) {
        this.info(`[transform] ${id}`);
        if (compilableFiles.has(id)) {
          return {
            code: compilableFiles.get(id),
            map: null,
          };
        } else {
          this.warn(`[transform] ${id} is not part of compilableFiles`);
        }
      }
    },
    watchChange: async function (id, change) {
      if (projectOptions) {
        if (id.endsWith(".fsproj")) {
          this.info("[watchChange] Should reload project");
        } else if (fsharpFileRegex.test(id)) {
          this.info(`[watchChange] ${id} changed`);
          try {
            /** @typedef {object} json
             * @property {string} Case
             * @property {string[]} Fields
             */
            const compilationResult = await endpoint.send("fable/compile", {
              fileName: id,
            });
            if (
              compilationResult.Case === "Success" &&
              compilationResult.Fields &&
              compilationResult.Fields.length > 0
            ) {
              this.info(`[watchChange] ${id} compiled`);
              const compiledFSharpFiles = compilationResult.Fields[0];
              const loadPromises = Object.keys(compiledFSharpFiles).map(
                (fsFile) => {
                  compilableFiles.set(fsFile, compiledFSharpFiles[fsFile]);
                  return this.load({ id: fsFile });
                },
              );
              return await Promise.all(loadPromises);
            } else {
              this.warn({
                message: `[watchChange] compilation of ${id} failed`,
                meta: {
                  error: compilationResult.Fields[0],
                },
              });
            }
          } catch (e) {
            this.error({
              message: `[watchChange] compilation of ${id} failed, plugin could not handle this gracefully`,
              cause: e,
            });
          }
        }
      }
    },
    handleHotUpdate: function ({ file, server, modules }) {
      if (fsharpFileRegex.test(file)) {
        const fileIdx = projectOptions.sourceFiles.indexOf(file);
        const sourceFiles = projectOptions.sourceFiles.filter(
          (f, idx) => idx >= fileIdx,
        );
        const logger = server.config.logger;
        logger.info(`[handleHotUpdate] ${file}`);
        const modulesToCompile = [];
        for (const sourceFile of sourceFiles) {
          const module = server.moduleGraph.getModuleById(sourceFile);
          if (module) {
            modulesToCompile.push(module);
          } else {
            logger.warn(`[handleHotUpdate] No module found for ${sourceFile}`);
          }
        }
        if (modulesToCompile.length > 0) {
          logger.info(
            `[handleHotUpdate] about to send HMR update (${modulesToCompile.length}) to client.`,
          );
          server.ws.send({
            type: "custom",
            event: "hot-update-dependents",
            data: modulesToCompile.map(({ url }) => url),
          });
          return modulesToCompile;
        } else {
          return modules;
        }
      }
    },
    buildEnd: () => {
      dotnetProcess.kill();
    },
  };
}
