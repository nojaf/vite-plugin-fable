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
    name: "vite-fable-plugin",
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
      const projectResponse = await getProjectFile(fsproj);
      if (
        projectResponse.Case === "Success" &&
        projectResponse.Fields &&
        projectResponse.Fields.length === 2
      ) {
        projectOptions = projectResponse.Fields[0];
        const compiledFSharpFiles = projectResponse.Fields[1];
        // for proj file
        //console.log("projectOption.sourceFiles", projectOptions.sourceFiles, compiledFSharpFiles);
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
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        try {
          const relativeUrl = req.originalUrl;
          const baseUrl = "http://localhost:5137"; // Dummy base URL
          const url = new URL(relativeUrl, baseUrl);

          if (url.pathname.endsWith(".fs")) {
            res.setHeader("Content-Type", "application/javascript");
            server.transformRequest(req.originalUrl).then((transformResult) => {
              res.end(transformResult.code);
            });
          } else {
            next();
          }
        } catch (e) {
          console.log(e);
          next();
        }
      });
    },
    // resolveId: async function (source, importer, options) {
    //   console.log(`resolveId ${source}`);
    //   //   // In this callback we want to resolve virtual javascript files and link them back together to the F# project.
    //   //   if (!source.endsWith(".js")) return null;
    //   //
    //   //   this.info({
    //   //     message: `[resolveId] ${source}`,
    //   //     meta: {
    //   //       importer,
    //   //     },
    //   //   });
    //   //   // A file from the fable_modules doesn't seem to respect the FileExtension from CliArgs
    //   //   let fsFile = source.endsWith(".fs.js")
    //   //     ? source.trimEnd().replace(".js", "")
    //   //     : source.replace(".js", ".fs");
    //   //
    //   //   // The incoming path might be a file requested from the dev-server.
    //   //   // If this is the case, we need to map it to the absolute path first.
    //   //   if (!projectOptions.sourceFiles.includes(fsFile) && importer) {
    //   //     // Might be /Library.fs
    //   //     const importerFolder = path.dirname(importer);
    //   //     const sourceRelativePath = source.startsWith("/")
    //   //       ? `.${fsFile}`
    //   //       : fsFile;
    //   //     fsFile = normalizePath(
    //   //       path.resolve(importerFolder, sourceRelativePath),
    //   //     );
    //   //     this.info(`[resolveId] Absolute path of resolved F# file: ${fsFile}`);
    //   //   }
    //   //
    //   //   if (projectOptions.sourceFiles.includes(fsFile)) {
    //   //     return fsFile.replace(fsharpFileRegex, ".js");
    //   //   }
    //   //
    //   return null;
    // },
    // load: async function (id) {
    //   this.info(`[load] ${id}`);
    //   if (!compilableFiles.has(id)) return null;
    //   this.info(`[load] ${id}`);
    //   return {
    //     code: compilableFiles.get(id),
    //   };
    // },
    watchChange: async function (id, change) {
      if (projectOptions) {
        if (id.endsWith(".fsproj")) {
          this.info("[watchChange] Should reload project");
        } else if (fsharpFileRegex.test(id)) {
          this.info(`[watchChange] ${id} changed`);
          const compilationResult = await endpoint.send("fable/compile", {
            fileName: id,
          });
          this.info(`[watchChange] ${id} compiled, ${compilationResult}`);
          const loadPromises = Object.keys(
            compilationResult.compiledFSharpFiles,
          ).map((fsFile) => {
            compilableFiles.set(
              fsFile,
              compilationResult.compiledFSharpFiles[fsFile],
            );
            return this.load({ id: fsFile });
          });
          await Promise.all(loadPromises);
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
