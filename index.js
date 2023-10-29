import {spawn} from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";
import {JSONRPCEndpoint} from "ts-lsp-client";

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const dotnetExe = path.join(currentDir, "Fable.Daemon\\bin\\Debug\\net8.0\\Fable.Daemon.exe",)
const dotnetProcess = spawn(dotnetExe, ['--stdio'], {
    shell: true, stdio: 'pipe'
});
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout,);
const fsharpFileRegex = /\.fsx?$/;

export default function fablePlugin({ fsproj }) {
    return {
        name: "vite-fable-plugin", 
        buildStart: async options => {
            const fableLibrary = path.join(process.cwd(), 'node_modules/fable-library')
            const fsharpOptions = await endpoint.send('fable/init', { project: fsproj, fableLibrary });
        },
        transform: async (src, id) => {
            // id is an fsharp file path that was imported from some place.
            if (fsharpFileRegex.test(id)){
                const js = await endpoint.send('fable/compile', {
                    fileName: id
                });
                return {
                    code: js
                }
            }
        },
        buildEnd: () => {
            dotnetProcess.kill();
        }
    }
}