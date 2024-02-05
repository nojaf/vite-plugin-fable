import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath } from "vite";

/**
 * @typedef {Object} FSharpDiscriminatedUnion
 * @property {string} case - The name of the case (will have same casing as in type definition).
 * @property {any[]} fields - The fields associated with the case.
 */

/**
 * @typedef {Object} FSharpProjectOptions
 * @property {string[]} sourceFiles
 */

const fsharpFileRegex = /\.(fs|fsi)$/;
const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fableDaemon = path.join(currentDir, "bin/Fable.Daemon.dll");
const dotnetProcess = spawn("dotnet", [fableDaemon, "--stdio"], {
  shell: true,
  stdio: "pipe",
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout);

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

/**
 * Retrieves the project file and its compiled files.
 * @param {string} configuration - Release or Debug
 * @param {string} project - The name or path of the project.
 * @returns {Promise<{projectOptions: FSharpProjectOptions, compiledFiles: Map<string, string>}>} A promise that resolves to an object containing the project options and compiled files.
 * @throws {Error} If the result from the endpoint is not a success case.
 */
async function getProjectFile(configuration, project) {
  const fableLibraryArray = await import.meta.resolve("fable-library/Array");
  const fableLibrary = path.dirname(fableLibraryArray);

  /** @type {FSharpDiscriminatedUnion} */
  const result = await endpoint.send("fable/init", {
    configuration,
    project,
    fableLibrary,
  });

  if (result.case === "Success") {
    return {
      projectOptions: result.fields[0],
      compiledFiles: result.fields[1],
    };
  } else {
    throw new Error(result.fields[0] || "Unknown error occurred");
  }
}

/**
 * @typedef {Object} PluginOptions
 * @property {string} [fsproj] - The main fsproj to load
 */

/**
 * @function
 * @param {PluginOptions} config - The options for configuring the plugin.
 * @description Initializes and returns a Vite plugin for to process the incoming F# project.
 * @returns {import('vite').Plugin} A Vite plugin object with the standard structure and hooks.
 */
export default function fablePlugin(config = {}) {
  /* @type {Map<string, string>} */
  const compilableFiles = new Map();
  /** @type {FSharpProjectOptions|null} */
  let projectOptions = null;
  /** @type {string|null} */
  let fsproj = null;
  /** @type {string} */
  let configuration = "Debug";

  return {
    name: "vite-plugin-fable",
    configResolved: async function (resolvedConfig) {
      configuration =
        resolvedConfig.env.MODE === "production" ? "Release" : "Debug";
      resolvedConfig.logger.info(
        `[configResolved] Configuration: ${configuration}`,
      );

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
      try {
        this.info(`[buildStart] Initial compile started of ${fsproj}`);
        const projectResponse = await getProjectFile(configuration, fsproj);
        this.info(`[buildStart] Initial compile completed of ${fsproj}`);
        projectOptions = projectResponse.projectOptions;
        const compiledFSharpFiles = projectResponse.compiledFiles;
        // for proj file
        projectOptions.sourceFiles.forEach((file) => {
          this.addWatchFile(file);
          compilableFiles.set(file, compiledFSharpFiles[file]);
        });
      } catch (e) {
        this.warn({
          message: "[buildStart] Unexpected projectResponse",
          meta: {
            error: e,
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
             * @property {string} case
             * @property {string[]} fields
             */
            const compilationResult = await endpoint.send("fable/compile", {
              fileName: id,
            });
            if (
              compilationResult.case === "Success" &&
              compilationResult.fields &&
              compilationResult.fields.length > 0
            ) {
              this.info(`[watchChange] ${id} compiled`);
              const compiledFSharpFiles = compilationResult.fields[0];
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
                  error: compilationResult.fields[0],
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
      if (
        projectOptions &&
        projectOptions.sourceFiles &&
        fsharpFileRegex.test(file)
      ) {
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
