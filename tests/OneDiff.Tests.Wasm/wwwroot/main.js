// Starts the .NET runtime, runs Main and publishes the exit code on window so the launcher can read it.
import { dotnet } from './_framework/dotnet.js';

const { getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

let exitCode = -1;
try {
    exitCode = await dotnet.run();
} catch (e) {
    console.log('HOST ERROR: ' + e);
}

getConfig();
window.__oneDiffExitCode = exitCode;
document.getElementById('out').textContent = 'exit code ' + exitCode;
