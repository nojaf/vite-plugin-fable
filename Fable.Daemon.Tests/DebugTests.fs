module Fable.Daemon.Tests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Nerdbank.Streams
open StreamJsonRpc
open Fable.Daemon

[<Test>]
let DebugTest () =
    task {
        Directory.SetCurrentDirectory ("/home/nojaf/projects/telplin/tool/client")

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()
        let daemon = new Program.FableServer (serverStream, serverStream)
        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let! response =
            daemon.Init
                {
                    Project = "/home/nojaf/projects/telplin/tool/client/OnlineTool.fsproj"
                    FableLibrary = "/home/nojaf/projects/telplin/tool/client//node_modules/fable-library"
                    Configuration = "Release"
                }

        // let! fileChangedResponse =
        //     daemon.CompileFile (
        //         {
        //             FileName = "/home/nojaf/projects/vite-plugin-fable/sample-project/Math.fs"
        //         }
        //     )
        // let! response =
        //     client.InvokeAsync<ProjectChangedResult> (
        //         "fable/init",
        //         {
        //             Project = @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\App.fsproj"
        //             FableLibrary =
        //                 @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\node_modules\fable-library"
        //         }
        //     )

        printfn "response: %A" response
        client.Dispose ()
        (daemon :> IDisposable).Dispose ()

        Assert.Pass ()
    }
