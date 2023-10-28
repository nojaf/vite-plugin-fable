import {spawn} from "child_process";
import {JSONRPCEndpoint} from "ts-lsp-client";
import path from "path";

const pwd = process.cwd();
const dotnetExe = path.join(pwd, "Fable.Daemon\\bin\\Debug\\net8.0\\Fable.Daemon.exe",)
const dotnetProcess = spawn(dotnetExe, ['--stdio'], {
    shell: true, stdio: 'pipe'
});
const projectFile = path.join(pwd, "sample-project/App.fsproj");
const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout,);
const result = await endpoint.send('fable/init', { project: projectFile });
console.log(result);
const js = await endpoint.send('fable/compile', {
    fileName: "C:\\Users\\nojaf\\Projects\\vite-plugin-fable\\sample-project\\Library.fs"
});
console.log(js)

process.on('SIGINT', function() {
    dotnetProcess.kill();
});

// C:\Users\nojaf\Projects\vite-plugin-fable\Fable.Daemon\bin\Debug\net8.0\Fable.Daemon.exe