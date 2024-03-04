import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath } from "vite";
import { transform } from "esbuild";
import { filter, map, bufferTime, Subject } from "rxjs";
import colors from "picocolors";
// https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Promise/withResolvers
import withResolvers from "promise.withresolvers";

withResolvers.shim();

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
 * @property {import('rxjs').Subscription|null} pendingChanges
 * @property {any} hotPromiseWithResolvers
 * @property {Boolean} isBuild
 */

/**
 * Represents an event where an F# file has changed.
 * @typedef {Object} FSharpFileChanged
 * @property {"FSharpFileChanged"} type - The type of the event, acts as a discriminator. Always "FSharpFileChanged" for this type.
 * @property {string} file - The file that changed.
 */

/**
 * Represents an event where a project file has changed.
 * @typedef {Object} ProjectFileChanged
 * @property {"ProjectFileChanged"} type - The type of the event, acts as a discriminator. Always "ProjectFileChanged" for this type.
 * @property {string} file - The file that changed.
 */

/**
 * Type that represents the possible hook events.
 * This is an equivalent to the F# DU `HookEvent`.
 * Use `type: "FSharpFileChanged"` for FSharpFileChanged events (and include the `file` property),
 * or `type: "ProjectFileChanged"` for ProjectFileChanged events.
 * @typedef {(FSharpFileChanged | ProjectFileChanged)} HookEvent
 */

/**
 * @typedef {Object} PendingChangesState
 * @property {Boolean} projectChanged
 * @property {Set<string>} fsharpFiles
 * @property {Set<string>} projectFiles
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
    dependentFiles: new Set(),
    // @ts-ignore
    logger: { info: console.log, warn: console.warn, error: console.error },
    dotnetProcess: null,
    endpoint: null,
    pendingChanges: null,
    hotPromiseWithResolvers: null,
    isBuild: false,
  };

  /** @type {Subject<HookEvent>} **/
  const pendingChangesSubject = new Subject();

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
   * @returns {Promise}
   */
  async function compileProject(addWatchFile) {
    logInfo("compileProject", `Full compile started of ${state.fsproj}`);
    const fableLibrary = await getFableLibrary();
    logDebug("compileProject", `fable-library located at ${fableLibrary}`);
    logInfo("compileProject", `about to type-checked ${state.fsproj}.`);
    const projectResponse = await getProjectFile(fableLibrary);
    logInfo("compileProject", `${state.fsproj} was type-checked.`);
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
    logInfo("compileProject", `Full compile completed of ${state.fsproj}`);
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
   * @param {Set<String>} projectFiles
   */
  async function projectChanged(addWatchFile, projectFiles) {
    try {
      logInfo(
        "projectChanged",
        `dependent file ${Array.from(projectFiles).join("\n")} changed.`,
      );
      state.sourceFiles.clear();
      state.compilableFiles.clear();
      state.dependentFiles.clear();
      await compileProject(addWatchFile);
    } catch (e) {
      logCritical(
        "projectChanged",
        `Unexpected failure during projectChanged for ${Array.from(projectFiles)},\n${e}`,
      );
    }
  }

  /**
   * F# files part of state.compilableFiles have changed.
   * @returns {Promise<void>}
   * @param {String[]} files
   */
  async function fsharpFileChanged(files) {
    try {
      /** @type {FSharpDiscriminatedUnion} */
      const compilationResult = await state.endpoint.send("fable/compile", {
        fileNames: files,
      });
      if (
        compilationResult.case === "Success" &&
        compilationResult.fields &&
        compilationResult.fields.length > 0
      ) {
        const compiledFSharpFiles = compilationResult.fields[0];

        logDebug(
          "fsharpFileChanged",
          `\n${Object.keys(compiledFSharpFiles).join("\n")} compiled`,
        );

        for (const [key, value] of Object.entries(compiledFSharpFiles)) {
          const normalizedFileName = normalizePath(key);
          state.compilableFiles.set(normalizedFileName, value);
        }

        const diagnostics = compilationResult.fields[1];
        logDiagnostics(diagnostics);
      } else {
        logError(
          "watchChange",
          `compilation of ${files} failed, ${compilationResult.fields[0]}`,
        );
      }
    } catch (e) {
      logCritical(
        "watchChange",
        `compilation of ${files} failed, plugin could not handle this gracefully. ${e}`,
      );
    }
  }

  /**
   * @param {PendingChangesState} acc
   * @param {HookEvent} e
   * @return {PendingChangesState}
   */
  function reducePendingChange(acc, e) {
    if (e.type === "FSharpFileChanged") {
      return {
        projectChanged: acc.projectChanged,
        fsharpFiles: acc.fsharpFiles.add(e.file),
        projectFiles: acc.projectFiles,
      };
    } else if (e.type === "ProjectFileChanged") {
      return {
        projectChanged: true,
        fsharpFiles: acc.fsharpFiles,
        projectFiles: acc.projectFiles.add(e.file),
      };
    } else {
      logWarn("pendingChanges", `Unexpected pending change ${e}`);
      return acc;
    }
  }

  return {
    name: "vite-plugin-fable",
    enforce: "pre",
    configResolved: async function (resolvedConfig) {
      state.logger = resolvedConfig.logger;
      state.configuration =
        resolvedConfig.env.MODE === "production" ? "Release" : "Debug";
      state.isBuild = resolvedConfig.command === "build";
      logDebug("configResolved", `Configuration: ${state.configuration}`);
      const configDir =
        resolvedConfig.configFile && path.dirname(resolvedConfig.configFile);

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

        if (state.isBuild) {
          await projectChanged(
            this.addWatchFile.bind(this),
            new Set([state.fsproj]),
          );
        } else {
          state.pendingChanges = pendingChangesSubject
            .pipe(
              bufferTime(50),
              map((events) => {
                return events.reduce(reducePendingChange, {
                  projectChanged: false,
                  fsharpFiles: new Set(),
                  projectFiles: new Set(),
                });
              }),
              filter(
                (state) => state.projectChanged || state.fsharpFiles.size > 0,
              ),
            )
            .subscribe(async (pendingChanges) => {
              if (pendingChanges.projectChanged) {
                await projectChanged(
                  this.addWatchFile.bind(this),
                  pendingChanges.projectFiles,
                );
              } else {
                const files = Array.from(pendingChanges.fsharpFiles);
                logDebug("subscribe", files.join("\n"));
                await fsharpFileChanged(files);
              }

              if (state.hotPromiseWithResolvers) {
                state.hotPromiseWithResolvers.resolve();
                state.hotPromiseWithResolvers = null;
              }
            });

          logDebug("buildStart", "Initial project file change!");
          state.hotPromiseWithResolvers = Promise.withResolvers();
          pendingChangesSubject.next({
            type: "ProjectFileChanged",
            file: state.fsproj,
          });
          await state.hotPromiseWithResolvers.promise;
        }
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
        pendingChangesSubject.next({ type: "ProjectFileChanged", file: id });
      }
    },
    handleHotUpdate: async function ({ file, server, modules }) {
      if (state.compilableFiles.has(file)) {
        logDebug("handleHotUpdate", `enter for ${file}`);
        pendingChangesSubject.next({
          type: "FSharpFileChanged",
          file: file,
        });

        // handleHotUpdate could be called concurrently because multiple files changed.
        if (!state.hotPromiseWithResolvers) {
          state.hotPromiseWithResolvers = Promise.withResolvers();
        }

        // The idea is to wait for a shared promise to resolve.
        // This will resolve in the subscription of state.changedFSharpFiles
        await state.hotPromiseWithResolvers.promise;
        logDebug("handleHotUpdate", `leave for ${file}`);
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
      if (state.pendingChanges) {
        state.pendingChanges.unsubscribe();
      }
    },
  };
}
