import {spawn} from "node:child_process";
import {fileURLToPath} from "node:url";
import path from "node:path";
import {JSONRPCEndpoint} from "ts-lsp-client";
import {normalizePath} from 'vite'

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const dotnetExe = path.join(currentDir, "Fable.Daemon\\bin\\Debug\\net8.0\\Fable.Daemon.exe",)
const dotnetProcess = spawn(dotnetExe, ['--stdio'], {
    shell: true, stdio: 'pipe'
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout,);
const fableLibrary = path.join(process.cwd(), 'node_modules/fable-library');

/** @typedef {object} json
 * @property {object} ProjectOptions
 * @property {string} ProjectOptions.ProjectFileName
 * @property {string[]} ProjectOptions.SourceFiles
 * @property {string[]} ProjectOptions.OtherOptions
 * @property {object} CompiledFSharpFiles
 */
async function getProjectFile(project) {
    return await endpoint.send('fable/init', {
        project,
        fableLibrary
    });
}

export default function fablePlugin({fsproj}) {
    const compilableFiles = new Map()
    let projectOptions = null;

    return {
        name: "vite-fable-plugin",
        buildStart: async function (options) {
            const projectResponse = await getProjectFile(fsproj);
            this.addWatchFile(normalizePath(fsproj));
            // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
            // for proj file
            projectOptions = projectResponse.ProjectOptions;
            console.log("projectOption.SourceFiles", projectOptions.SourceFiles, projectResponse.CompiledFSharpFiles);
            projectOptions.SourceFiles.forEach(file => {
                // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
                const jsFile = file.replace('.fs','.js');
                compilableFiles.set(jsFile, projectResponse.CompiledFSharpFiles[file])
            });
        },
        resolveId: async function (source, importer, options) {
            console.info("resolveId", source)
            const jsFile = normalizePath(
                path.resolve(
                    importer ?
                        path.join(path.dirname(importer), source) :
                        source
                )
            );
            if (!jsFile.endsWith('.js')) return null;
            const fsFile = jsFile.replace('.js', '.fs');
            console.log("fsFile", fsFile, projectOptions.SourceFiles, projectOptions.SourceFiles.includes(fsFile))
            if (!(projectOptions || !projectOptions.SourceFiles.includes(fsFile))) return null;
            return jsFile;
        },
        load: async function (id) {
            console.log("loading", id);
            if (!compilableFiles.has(id)) return null;
            return {
                code: compilableFiles.get(id)
            }
        },
        watchChange: async function (id, change) {
            console.log("watchChange", id, change);
            return null;
        },
        buildEnd: () => {
            dotnetProcess.kill();
        }
    }
}