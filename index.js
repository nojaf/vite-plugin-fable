import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath } from "vite";
import { transform } from "esbuild";
import { Subject } from "rxjs";
import { bufferTime, filter, map } from "rxjs/operators";
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

/** @typedef {Object} PluginState
 * @property {PluginOptions} config
 * @property {import('vite').Logger} logger
 * @property {import("node:child_process").ChildProcessWithoutNullStreams|null} dotnetProcess
 * @property {JSONRPCEndpoint|null} endpoint
 * @property {Map<string, string>} compilableFiles
 * @property {Set<string>} sourceFiles
 * @property {string|null} fsproj
 * @property {string} configuration
 * @property {Set<string>} dependentFiles
 * @property {import('rxjs').Subscription|null} changedFSharpFiles
 */

const fsharpFileRegex = /\.(fs|fsx)$/;
const currentDir = path.dirname(fileURLToPath(import.meta.url));
const fableDaemon = path.join(currentDir, "bin/Fable.Daemon.dll");

if (process.env.VITE_PLUGIN_FABLE_DEBUG) {
  console.log(
    `Running daemon in debug mode, visit http://localhost:9014 to view logs`,
  );
}

/**
 * @typedef {Object} PluginOptions
 * @property {string} [fsproj] - The main fsproj to load
 * @property {'transform' | 'preserve' | 'automatic' | null} [jsx] - Apply JSX transformation after Fable compilation: https://esbuild.github.io/api/#transformation
 * @property {Boolean} [noReflection] - Pass noReflection value to Fable.Compiler
 * @property {string[]} [exclude] - Pass exclude to Fable.Compiler
 */

/** @type {PluginOptions} */
const defaultConfig = { jsx: null, noReflection: false, exclude: [] };

/**
 * @function
 * @param {PluginOptions} userConfig - The options for configuring the plugin.
 * @description Initializes and returns a Vite plugin for to process the incoming F# project.
 * @returns {import('vite').Plugin} A Vite plugin object with the standard structure and hooks.
 */
export default function fablePlugin(userConfig) {
  /** @type {PluginState} */
  const state = {
    config: Object.assign({}, defaultConfig, userConfig),
    compilableFiles: new Map(),
    sourceFiles: new Set(),
    fsproj: null,
    configuration: "Debug",
    dependentFiles: new Set([]),
    // @ts-ignore
    logger: { info: console.log, warn: console.warn, error: console.error },
    dotnetProcess: null,
    endpoint: null,
    changedFSharpFiles: null,
  };
  const fsharpChangedFilesSubject = new Subject();

  /**
   * @param {String} prefix
   * @param {String} message
   */
  function logDebug(prefix, message) {
    state.logger.info(colors.dim(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   * @param {String} prefix
   * @param {String} message
   */
  function logInfo(prefix, message) {
    state.logger.info(colors.green(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   * @param {String} prefix
   * @param {String} message
   */
  function logWarn(prefix, message) {
    state.logger.warn(colors.yellow(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   * @param {String} prefix
   * @param {String} message
   */
  function logError(prefix, message) {
    state.logger.warn(colors.red(`[fable] ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   * @param {String} prefix
   * @param {String} message
   */
  function logCritical(prefix, message) {
    state.logger.error(colors.red(`[fable] ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   @param {string} configDir - Folder path of the vite.config.js file.
   */
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
    const fableLibraryInOwnNodeModules = path.join(
      currentDir,
      "node_modules/@fable-org/fable-library-js",
    );
    try {
      await fs.access(fableLibraryInOwnNodeModules, fs.constants.F_OK);
      return normalizePath(fableLibraryInOwnNodeModules);
    } catch (e) {
      return normalizePath(
        path.join(currentDir, "../@fable-org/fable-library-js"),
      );
    }
  }

  /**
   * Retrieves the project file. At this stage the project is type-checked but Fable did not compile anything.
   * @param {string} fableLibrary - Location of the fable-library node module.
   * @returns {Promise<{sourceFiles: string[], diagnostics: Diagnostic[], dependentFiles: string[]}>} A promise that resolves to an object containing the project options and compiled files.
   * @throws {Error} If the result from the endpoint is not a success case.
   */
  async function getProjectFile(fableLibrary) {
    /** @type {FSharpDiscriminatedUnion} */
    const result = await state.endpoint.send("fable/project-changed", {
      configuration: state.configuration,
      project: state.fsproj,
      fableLibrary,
      exclude: state.config.exclude,
      noReflection: state.config.noReflection,
    });

    if (result.case === "Success") {
      return {
        sourceFiles: result.fields[0],
        diagnostics: result.fields[1],
        dependentFiles: result.fields[2],
      };
    } else {
      throw new Error(result.fields[0] || "Unknown error occurred");
    }
  }

  /**
   * Try and compile the entire project using Fable. The daemon contains all the information at this point to do this.
   * No need to pass any additional info.
   * @returns {Promise<Map<string, string>>} A promise that resolves a map of compiled files.
   * @throws {Error} If the result from the endpoint is not a success case.
   */
  async function tryInitialCompile() {
    /** @type {FSharpDiscriminatedUnion} */
    const result = await state.endpoint.send("fable/initial-compile");

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
   * @param {Diagnostic[]} diagnostics - An array of Diagnostic objects to be logged.
   */
  function logDiagnostics(diagnostics) {
    for (const diagnostic of diagnostics) {
      switch (diagnostic.severity.toLowerCase()) {
        case "error":
          logError("", formatDiagnostic(diagnostic));
          break;
        case "warning":
          logWarn("", formatDiagnostic(diagnostic));
          break;
        default:
          logInfo("", formatDiagnostic(diagnostic));
          break;
      }
    }
  }

  /**
   * Does a type-check and compilation of the state.fsproj
   * @function
   * @param {function} addWatchFile
   * @param {String} sourceHook
   * @returns {Promise}
   */
  async function compileProject(addWatchFile, sourceHook) {
    logInfo(sourceHook, `Full compile started of ${state.fsproj}`);
    const fableLibrary = await getFableLibrary();
    logInfo(sourceHook, `fable-library located at ${fableLibrary}`);
    logInfo(sourceHook, `about to type-checked ${state.fsproj}.`);
    const projectResponse = await getProjectFile(fableLibrary);
    logInfo(sourceHook, `${state.fsproj} was type-checked.`);
    logDiagnostics(projectResponse.diagnostics);
    for (const sf of projectResponse.sourceFiles) {
      state.sourceFiles.add(normalizePath(sf));
    }
    for (let dependentFile of projectResponse.dependentFiles) {
      dependentFile = normalizePath(dependentFile);
      state.dependentFiles.add(dependentFile);
      addWatchFile(dependentFile);
    }
    const compiledFSharpFiles = await tryInitialCompile();
    logInfo(sourceHook, `Full compile completed of ${state.fsproj}`);
    state.sourceFiles.forEach((file) => {
      addWatchFile(file);
      const normalizedFileName = normalizePath(file);
      state.compilableFiles.set(normalizedFileName, compiledFSharpFiles[file]);
    });
  }

  /**
   * Either the project or a dependent file changed
   * @returns {Promise<void>}
   * @param {function} addWatchFile
   * @param {String} sourceHook
   * @param {String} id
   */
  async function projectChanged(addWatchFile, sourceHook, id) {
    try {
      logInfo(sourceHook, `dependent file ${id} changed.`);
      state.sourceFiles.clear();
      state.compilableFiles.clear();
      state.dependentFiles.clear();
      await compileProject(addWatchFile, sourceHook);
    } catch (e) {
      logCritical(
        sourceHook,
        `Unexpected failure during projectChanged for ${id}`,
      );
    }
  }

  /**
   * An F# file part of state.compilableFiles has changed.
   * @returns {Promise<void>}
   * @param {function} load
   * @param {String} id
   */
  async function fsharpFileChanged(load, id) {
    try {
      logInfo("watchChange", `${id} changed`);
      /** @type {FSharpDiscriminatedUnion} */
      const compilationResult = await state.endpoint.send("fable/compile", {
        fileName: id,
      });
      if (
        compilationResult.case === "Success" &&
        compilationResult.fields &&
        compilationResult.fields.length > 0
      ) {
        logInfo("watchChange", `${id} compiled`);
        const compiledFSharpFiles = compilationResult.fields[0];
        const diagnostics = compilationResult.fields[1];
        logDiagnostics(diagnostics);
        const loadPromises = Object.keys(compiledFSharpFiles).map((fsFile) => {
          const normalizedFileName = normalizePath(fsFile);
          state.compilableFiles.set(
            normalizedFileName,
            compiledFSharpFiles[fsFile],
          );
          return load({ id: normalizedFileName });
        });
        await Promise.all(loadPromises);
      } else {
        logError(
          "watchChange",
          `compilation of ${id} failed, ${compilationResult.fields[0]}`,
        );
      }
    } catch (e) {
      logCritical(
        "watchChange",
        `compilation of ${id} failed, plugin could not handle this gracefully. ${e}`,
      );
    }
  }

  return {
    name: "vite-plugin-fable",
    enforce: "pre",
    configResolved: async function (resolvedConfig) {
      state.logger = resolvedConfig.logger;
      state.configuration =
        resolvedConfig.env.MODE === "production" ? "Release" : "Debug";
      logDebug("configResolved", `Configuration: ${state.configuration}`);
      const configDir = path.dirname(resolvedConfig.configFile);

      if (state.config && state.config.fsproj) {
        state.fsproj = state.config.fsproj;
      } else {
        state.fsproj = await findFsProjFile(configDir);
      }

      if (!state.fsproj) {
        logCritical(
          "configResolved",
          `No .fsproj file was found in ${configDir}`,
        );
      } else {
        logInfo("configResolved", `Entry fsproj ${state.fsproj}`);
      }
    },
    buildStart: async function (options) {
      try {
        logInfo("buildStart", "Starting daemon");
        state.dotnetProcess = spawn("dotnet", [fableDaemon, "--stdio"], {
          shell: true,
          stdio: "pipe",
        });
        state.endpoint = new JSONRPCEndpoint(
          state.dotnetProcess.stdin,
          state.dotnetProcess.stdout,
        );
        state.changedFSharpFiles = fsharpChangedFilesSubject
          .pipe(
            bufferTime(50),
            map((changes) => new Set(changes)),
            filter((changes) => changes.size > 0),
          )
          .subscribe(async (changedFSharpFiles) => {
            const files = Array.from(changedFSharpFiles);
            logDebug("subscribe", files.join(","));
            const last = files.findLast(() => true);
            await fsharpFileChanged(this.load.bind(this), last);
          });
        await compileProject(this.addWatchFile.bind(this), "buildStart");
      } catch (e) {
        logCritical("buildStart", `Unexpected failure during buildStart: ${e}`);
      }
    },
    transform: async function (src, id) {
      if (fsharpFileRegex.test(id)) {
        logDebug("transform", id);
        if (state.compilableFiles.has(id)) {
          let code = state.compilableFiles.get(id);
          // If Fable outputted JSX, we still need to transform this.
          // @vitejs/plugin-react does not do this.
          if (state.config.jsx) {
            const esbuildResult = await transform(code, {
              loader: "jsx",
              jsx: state.config.jsx,
            });
            code = esbuildResult.code;
          }
          return {
            code: code,
            map: null,
          };
        } else {
          logWarn("transform", `${id} is not part of compilableFiles.`);
        }
      }
    },
    watchChange: async function (id, change) {
      if (state.sourceFiles.size !== 0 && state.dependentFiles.has(id)) {
        await projectChanged(this.addWatchFile.bind(this), "watchChange", id);
      } else if (fsharpFileRegex.test(id) && state.compilableFiles.has(id)) {
        fsharpChangedFilesSubject.next(id);
      }
    },
    handleHotUpdate: function ({ file, server, modules }) {
      if (state.compilableFiles.has(file)) {
        // Potentially a file that is not imported in the current graph was changed.
        // Vite should not try and hot update that module.
        return modules.filter((m) => m.importers.size !== 0);
      }
    },
    buildEnd: () => {
      logInfo("buildEnd", "Closing daemon");
      if (state.dotnetProcess) {
        state.dotnetProcess.kill();
      }
      if (state.changedFSharpFiles) {
        state.changedFSharpFiles.unsubscribe();
      }
    },
  };
}
