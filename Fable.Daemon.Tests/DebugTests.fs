module Fable.Daemon.Tests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Nerdbank.Streams
open StreamJsonRpc
open Fable.Daemon

type Path with
    static member CombineNormalize ([<ParamArray>] parts : string array) = Path.Combine parts |> Path.GetFullPath

let fableLibrary =
    Path.CombineNormalize (__SOURCE_DIRECTORY__, "../node_modules/@fable-org/fable-library-js")

let sampleApp =
    {
        Project = Path.CombineNormalize (__SOURCE_DIRECTORY__, "sample-project/App.fsproj")
        FableLibrary = fableLibrary
        Configuration = "Release"
    }

let telplin =

    {
        Project = Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../telplin/tool/client/OnlineTool.fsproj")
        FableLibrary = fableLibrary
        Configuration = "Debug"
    }

let fantomasTools =
    {
        Project =
            Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../fantomas-tools/src/client/fsharp/FantomasTools.fsproj")
        FableLibrary = fableLibrary
        Configuration = "Debug"
    }

[<Test>]
let DebugTest () =
    task {
        let config = fantomasTools
        Directory.SetCurrentDirectory (FileInfo(config.Project).DirectoryName)

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()
        let daemon = new Program.FableServer (serverStream, serverStream)
        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let! typecheckResponse = daemon.ProjectChanged config
        ignore typecheckResponse
        let! initialCompile = daemon.InitialCompile ()

        printfn "response: %A" initialCompile
        client.Dispose ()
        (daemon :> IDisposable).Dispose ()

        Assert.Pass ()
    }
