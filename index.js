import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath } from "vite";
import colors from "picocolors";

/**
 * @typedef {Object} FSharpDiscriminatedUnion
 * @property {string} case - The name of the case (will have same casing as in type definition).
 * @property {any[]} fields - The fields associated with the case.
 */

/**
 * @typedef {Object} FSharpProjectOptions
 * @property {string[]} sourceFiles
 */

/**
 * @typedef {Object} DiagnosticRange
 * @property {number} startLine - The start line of the diagnostic range
 * @property {number} startColumn - The start column of the diagnostic range
 * @property {number} endLine - The end line of the diagnostic range
 * @property {number} endColumn - The end column of the diagnostic range
 */

/**
 * @typedef {Object} Diagnostic
 * @property {string} errorNumberText - The error number text
 * @property {string} message - The diagnostic message
 * @property {DiagnosticRange} range - The range where the diagnostic occurs
 * @property {string} severity - The severity of the diagnostic
 * @property {string} fileName - The file name where the diagnostic is found
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
 @returns {Promise<string>}
 */
async function getFableLibrary() {
  return "/home/nojaf/projects/Fable/temp/fable-library";
  // const fableLibraryInOwnNodeModules = path.join(
  //   currentDir,
  //   "node_modules/fable-library",
  // );
  // try {
  //   await fs.access(fableLibraryInOwnNodeModules, fs.constants.F_OK);
  //   return normalizePath(fableLibraryInOwnNodeModules);
  // } catch (e) {
  //   return normalizePath(path.join(currentDir, "../fable-library"));
  // }
}

/**
 * Retrieves the project file. At this stage the project is type-checked but Fable did not compile anything.
 * @param {string} fableLibrary - Location of the fable-library node module.
 * @param {string} configuration - Release or Debug
 * @param {string} project - The name or path of the project.
 * @returns {Promise<{projectOptions: FSharpProjectOptions, diagnostics: Diagnostic[]>} A promise that resolves to an object containing the project options and compiled files.
 * @throws {Error} If the result from the endpoint is not a success case.
 */
async function getProjectFile(fableLibrary, configuration, project) {
  /** @type {FSharpDiscriminatedUnion} */
  const result = await endpoint.send("fable/project-changed", {
    configuration,
    project,
    fableLibrary,
  });

  if (result.case === "Success") {
    return {
      projectOptions: result.fields[0],
      diagnostics: result.fields[1],
    };
  } else {
    throw new Error(result.fields[0] || "Unknown error occurred");
  }
}

/**
 * Try and compile the entire project using Fable. The daemon contains all the information at this point to do this.
 * No need to pass any additional info.
 * @returns {Promise<{Map<string, string>>} A promise that resolves a map of compiled files.
 * @throws {Error} If the result from the endpoint is not a success case.
 */
async function tryInitialCompile() {
  /** @type {FSharpDiscriminatedUnion} */
  const result = await endpoint.send("fable/initial-compile");

  if (result.case === "Success") {
    return result.fields[0];
  } else {
    throw new Error(result.fields[0] || "Unknown error occurred");
  }
}

/**
 * @function
 * @param {Diagnostic} diagnostic
 * @returns {string}
 */
function formatDiagnostic(diagnostic) {
  return `${diagnostic.severity.toUpperCase()} ${diagnostic.errorNumberText}: ${diagnostic.message} ${diagnostic.fileName} (${diagnostic.range.startLine},${diagnostic.range.startColumn}) (${diagnostic.range.endLine},${diagnostic.range.endColumn})`;
}

/**
 * @function
 * @param {Object} logger - The logger object with error, warn, and info methods.
 * @param {Diagnostic[]} diagnostics - An array of Diagnostic objects to be logged.
 */
function logDiagnostics(logger, diagnostics) {
  for (const diagnostic of diagnostics) {
    switch (diagnostic.severity.toLowerCase()) {
      case "error":
        logger.warn(colors.red(formatDiagnostic(diagnostic)), {
          timestamp: true,
        });
        break;
      case "warning":
        logger.warn(colors.yellow(formatDiagnostic(diagnostic)), {
          timestamp: true,
        });
        break;
      default:
        logger.info(formatDiagnostic(diagnostic), { timestamp: true });
        break;
    }
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

  let logger = { info: console.log, warn: console.warn, error: console.error };

  return {
    name: "vite-plugin-fable",
    configResolved: async function (resolvedConfig) {
      logger = resolvedConfig.logger;
      configuration =
        resolvedConfig.env.MODE === "production" ? "Release" : "Debug";
      logger.info(colors.blue(`[fable] Configuration: ${configuration}`), {
        timestamp: true,
        preface: "fable",
      });

      const configDir = path.dirname(resolvedConfig.configFile);

      if (config && config.fsproj) {
        fsproj = config.fsproj;
      } else {
        fsproj = await findFsProjFile(configDir);
      }

      if (!fsproj) {
        logger.error(
          colors.red(`[fable] No .fsproj file was found in ${configDir}`),
          { timestamp: true },
        );
      } else {
        logger.info(colors.blue(`[fable] Entry fsproj ${fsproj}`), {
          timestamp: true,
        });
      }
    },
    buildStart: async function (options) {
      try {
        logger.info(
          colors.blue(`[fable] Initial compile started of ${fsproj}`),
          { timestamp: true },
        );
        const fableLibrary = await getFableLibrary();
        logger.info(
          colors.blue(`[fable] fable-library located at ${fableLibrary}`),
          { timestamp: true },
        );
        const projectResponse = await getProjectFile(
          fableLibrary,
          configuration,
          fsproj,
        );
        logger.info(colors.blue(`[buildStart] ${fsproj} was type-checked.`), {
          timestamp: true,
        });
        logDiagnostics(this, projectResponse.diagnostics);
        projectOptions = projectResponse.projectOptions;

        const compiledFSharpFiles = await tryInitialCompile();
        logger.info(
          colors.blue(`[buildStart] Initial compile completed of ${fsproj}`),
          { timestamp: true },
        );
        // for proj file
        projectOptions.sourceFiles.forEach((file) => {
          this.addWatchFile(file);
          compilableFiles.set(file, compiledFSharpFiles[file]);
        });
      } catch (e) {
        logger.error(colors.red(`[fable] Unexpected projectResponse: ${e}`), {
          timestamp: true,
        });
      }
    },
    transform(src, id) {
      if (fsharpFileRegex.test(id)) {
        logger.info(`[fable] transform: ${id}`, { timestamp: true });
        if (compilableFiles.has(id)) {
          return {
            code: compilableFiles.get(id),
            map: null,
          };
        } else {
          logger.warn(
            colors.yellow(
              `[fable] transform: ${id} is not part of compilableFiles`,
            ),
            { timestamp: true },
          );
        }
      }
    },
    watchChange: async function (id, change) {
      if (projectOptions) {
        if (id.endsWith(".fsproj")) {
          logger.info("[fable] watch: Should reload project");
        } else if (fsharpFileRegex.test(id)) {
          logger.info(`[fable] watch: ${id} changed`);
          try {
            /** @type {FSharpDiscriminatedUnion} */
            const compilationResult = await endpoint.send("fable/compile", {
              fileName: id,
            });
            if (
              compilationResult.case === "Success" &&
              compilationResult.fields &&
              compilationResult.fields.length > 0
            ) {
              logger.info(`[fable] watch: ${id} compiled`);
              const compiledFSharpFiles = compilationResult.fields[0];
              const diagnostics = compilationResult.fields[1];
              logDiagnostics(logger, diagnostics);
              const loadPromises = Object.keys(compiledFSharpFiles).map(
                (fsFile) => {
                  compilableFiles.set(fsFile, compiledFSharpFiles[fsFile]);
                  return this.load({ id: fsFile });
                },
              );
              return await Promise.all(loadPromises);
            } else {
              logger.error(
                colors.red(
                  `[watchChange] compilation of ${id} failed, ${compilationResult.fields[0]}`,
                ),
                { timestamp: true },
              );
            }
          } catch (e) {
            logger.error(
              colors.red(
                `[watchChange] compilation of ${id} failed, plugin could not handle this gracefully. ${e}`,
              ),
              { timestamp: true },
            );
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
