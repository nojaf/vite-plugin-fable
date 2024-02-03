import { exec as execCallback } from 'child_process';
import { promisify } from 'util';
import os from 'os';

const exec = promisify(execCallback);

const platform = os.platform();
const arch = os.arch();
let rid = '';

switch (platform) {
  case 'win32':
    rid = 'win-';
    break;
  case 'darwin':
    rid = 'osx-';
    break;
  case 'linux':
    rid = 'linux-';
    break;
  default:
    console.error(`Unsupported platform: ${platform}`);
    process.exit(1);
}

switch (arch) {
  case 'x64':
    rid += 'x64'
    break;
  case 'arm64':
    rid += 'arm64';
    break;
  default:
    console.error(`Unsupported architecture: ${arch}`);
    process.exit(1);
}

console.log("About to publish Fable.Daemon");
const command = `dotnet publish Fable.Daemon/Fable.Daemon.fsproj --nologo -c Release -r ${rid} -p:PublishReadyToRun=true -o ./bin`;

async function run() {
  try {
    const { stdout, stderr } = await exec(command);
    console.log(stdout);
    if (stderr) {
      console.error(stderr);
    }
  } catch (err) {
    console.error(`Error: ${err}`);
  }
}

run();
