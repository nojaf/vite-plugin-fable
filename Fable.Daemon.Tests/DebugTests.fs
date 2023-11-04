module Fable.Daemon.Tests

open System
open System.Diagnostics
open NUnit.Framework
open Nerdbank.Streams
open StreamJsonRpc
open Fable.Daemon

[<Test>]
let DebugTest () =
    task {
        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()
        let daemon = new Program.FableServer (serverStream, serverStream)
        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let! response =
            client.InvokeAsync<ProjectChangedResult> (
                "fable/init",
                {
                    Project = @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\App.fsproj"
                    FableLibrary =
                        @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\node_modules\fable-library"
                }
            )

        client.Dispose ()
        (daemon :> IDisposable).Dispose ()

        Assert.Pass ()
    }
