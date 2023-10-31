import {spawn} from "node:child_process";
import {fileURLToPath} from "node:url";
import path from "node:path";
import {JSONRPCEndpoint} from "ts-lsp-client";
import { normalizePath } from 'vite'

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const dotnetExe = path.join(currentDir, "Fable.Daemon\\bin\\Debug\\net8.0\\Fable.Daemon.exe",)
const dotnetProcess = spawn(dotnetExe, ['--stdio'], {
    shell: true, stdio: 'pipe'
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout,);
const fsharpFileRegex = /\.fsx?$/;

export default function fablePlugin({fsproj}) {
    const compilableFiles = new Map()
    let projectOptions = null;

    return {
        name: "vite-fable-plugin",
        buildStart: async function (options) {
            const fableLibrary = path.join(process.cwd(), 'node_modules/fable-library')
            const {ProjectOptions, CompiledFSharpFiles} = await endpoint.send('fable/init', {
                project: fsproj,
                fableLibrary
            });
            // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
            // for proj file
            projectOptions = ProjectOptions;
            console.log("projectOption.SourceFiles", projectOptions.SourceFiles);
            Object.keys(CompiledFSharpFiles).forEach(file => {
                // TODO: addWatchFile, see https://rollupjs.org/plugin-development/#this-addwatchfile
                compilableFiles.set(file, CompiledFSharpFiles[file])
            });
        },
        resolveId: async function (source, importer, options) {
            console.info("resolveId", path.dirname(importer))
            const jsFile = normalizePath(path.resolve(path.join(path.dirname(importer), source)));
            if (!jsFile.endsWith('.js')) return null;
            const fsFile = jsFile.replace('.js', '.fs');
            console.log("fsFile", fsFile, projectOptions.SourceFiles, projectOptions.SourceFiles.includes(fsFile))
            if (!(projectOptions || !projectOptions.SourceFiles.includes(fsFile))) return null;
            return jsFile;
        },
        load: async function (id) {
            console.log("loading", id);
            if(!compilableFiles.has(id)) return null;
            return {
                code:compilableFiles.get(id)
            }
        },
        // transform: async (src, id) => {
        //     // id is an fsharp file path that was imported from some place.
        //     if (fsharpFileRegex.test(id)){
        //         const js = await endpoint.send('fable/compile', {
        //             fileName: id
        //         });
        //         return {
        //             code: js
        //         }
        //     }
        // },
        buildEnd: () => {
            dotnetProcess.kill();
        }
    }
}