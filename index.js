import {spawn} from "node:child_process";
import {fileURLToPath} from "node:url";
import path from "node:path";
import {JSONRPCEndpoint} from "ts-lsp-client";
import {normalizePath} from 'vite'

const fsharpFileRegex = /\.(fs|fsi)$/;
const currentDir = path.dirname(fileURLToPath(import.meta.url));
const dotnetExe = path.join(currentDir, "Fable.Daemon\\bin\\Debug\\net8.0\\Fable.Daemon.exe",)
const dotnetProcess = spawn(dotnetExe, ['--stdio'], {
    shell: true, stdio: 'pipe'
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout,);
const fableLibrary = path.join(process.cwd(), 'node_modules/fable-library');

/** @typedef {object} json
 * @property {object} projectOptions
 * @property {string} projectOptions.projectFileName
 * @property {string[]} projectOptions.sourceFiles
 * @property {string[]} projectOptions.otherOptions
 * @property {object} compiledFSharpFiles
 */
async function getProjectFile(project) {
    return await endpoint.send('fable/init', {
        project,
        fableLibrary
    });
}

export default function fablePlugin({fsproj}) {
    // A map of <js filePath, code>
    const compilableFiles = new Map()
    let projectOptions = null;

    return {
        name: "vite-fable-plugin",
        buildStart: async function (options) {
            const projectResponse = await getProjectFile(fsproj);
            console.log("projectResponse", projectResponse);
            // this.addWatchFile(normalizePath(fsproj));
            // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
            // for proj file
            projectOptions = projectResponse.projectOptions;
            console.log("projectOption.SourceFiles", projectOptions.sourceFiles, projectResponse.compiledFSharpFiles);
            projectOptions.sourceFiles.forEach(file => {
                // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
                const jsFile = file.replace('.fs','.js');
                compilableFiles.set(jsFile, projectResponse.compiledFSharpFiles[file])
            });
        },
        resolveId: async function (source, importer, options) {
            if (!source.endsWith('.js')) return null;
            console.info("resolveId", source, importer)
            let fsFile = source.replace('.js', '.fs');
            if(!projectOptions.sourceFiles.includes(fsFile) && importer) {
                // Might be /Library.fs
                const importerFolder = path.dirname(importer)
                const sourceRelativePath = source.startsWith('/') ? `.${fsFile}` : fsFile;
                console.log("path.resolve", path.resolve(importerFolder, sourceRelativePath))
                fsFile = normalizePath(path.resolve(importerFolder, sourceRelativePath));
            }

            if(projectOptions.sourceFiles.includes(fsFile)) {
                console.log("fsfile found", fsFile)
                return fsFile.replace(fsharpFileRegex ,'.js');
            }

            return null;
        },
        load: async function (id) {
            console.log("loading", id, compilableFiles.has(id));
            if (!compilableFiles.has(id)) return null;
            return {
                code: compilableFiles.get(id)
            }
        },
        watchChange: async function (id, change) {
            console.log("watchChange", id, change);
            if (id.endsWith('.fsproj')){
                console.log("Should reload project")
            }
            else if (fsharpFileRegex.test(id)) {
                console.log("file changed");
                const compilationResult = await endpoint.send("fable/compile", { fileName: id });
                console.log(compilationResult);
                const loadPromises =
                    Object.keys(compilationResult.compiledFSharpFiles).map(fsFile => {
                        const jsFile = fsFile.replace(fsharpFileRegex ,'.js')
                        console.log("jsFile XYZ", jsFile);
                        compilableFiles.set(jsFile, compilationResult.compiledFSharpFiles[fsFile]);
                        return this.load({ id:jsFile });
                    });
                await Promise.all(loadPromises);
            }
        },
        handleHotUpdate({ file, server, modules }) {
            if(fsharpFileRegex.test(file)) {
                const fileIdx = projectOptions.sourceFiles.indexOf(file);
                const sourceFiles = projectOptions.sourceFiles.filter((f,idx) => idx >= fileIdx);
                console.log(file, sourceFiles)
                const modulesToCompile = [];
                for (const sourceFile of sourceFiles) {
                    const jsFile = sourceFile.replace(fsharpFileRegex ,'.js');
                    const module = server.moduleGraph.getModuleById(jsFile)
                    if (module) modulesToCompile.push(module)
                }
                if (modulesToCompile.length > 0) {
                    server.ws.send({
                        type: 'custom',
                        event: 'hot-update-dependents',
                        data: modulesToCompile.map(({ url }) => url),
                    })
                    return modulesToCompile
                } else {
                    return modules
                }               
            }
        },
        buildEnd: () => {
            dotnetProcess.kill();
        }
    }
}