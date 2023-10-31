module Fable.Daemon.Tests

open System.Diagnostics
open NUnit.Framework
open StreamJsonRpc
open Fable.Daemon

[<Test>]
let DebugTest () =
    task {
        let processStart =
            let ps =
                ProcessStartInfo (
                    @"C:\Users\nojaf\Projects\vite-plugin-fable\Fable.Daemon\bin\Debug\net8.0\Fable.Daemon.exe"
                )

            ps.WorkingDirectory <- __SOURCE_DIRECTORY__
            ps

        processStart.UseShellExecute <- false
        processStart.RedirectStandardInput <- true
        processStart.RedirectStandardOutput <- true
        processStart.RedirectStandardError <- true
        processStart.CreateNoWindow <- true

        let daemonProcess = Process.Start processStart

        let client =
            new JsonRpc (daemonProcess.StandardInput.BaseStream, daemonProcess.StandardOutput.BaseStream)

        do client.StartListening ()

        // Attach to process here.

        let! response =
            client.InvokeAsync<ProjectChangedResult> (
                "fable/init",
                {
                    Project = @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\App.fsproj"
                    FableLibrary =
                        @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\node_modules\fable-library"
                }
            )

        Assert.Pass ()
    }
