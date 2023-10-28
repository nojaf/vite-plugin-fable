﻿open System
open System.IO
open System.Threading.Tasks
open Fable
open StreamJsonRpc
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceCodeServices
open Fable.Compiler.Service.ProjectCracker
open Fable.Compiler.Service.Util
open Fable.Daemon

type InitPayload =
    {
        /// Absolute path of fsproj
        Project : string
    }

type PingPayload = { Msg : string }

type CompileFilePayload = { FileName : string }

type Msg =
    | ProjectChanged of fsproj : string * AsyncReplyChannel<FSharpProjectOptions>
    | CompileFile of fileName : string * AsyncReplyChannel<string>
    | Disconnect

type Model =
    {
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        Compilers : Map<string, Compiler>
    }

let cliArgs : CliArgs =
    {
        ProjectFile = @"C:\Users\nojaf\Projects\MyFableApp\App.fsproj"
        RootDir = @"C:\Users\nojaf\Projects\MyFableApp"
        OutDir = None
        IsWatch = false
        Precompile = false
        PrecompiledLib = None
        PrintAst = false
        FableLibraryPath = None
        Configuration = "Release"
        NoRestore = true
        NoCache = true
        NoParallelTypeCheck = false
        SourceMaps = false
        SourceMapsRoot = None
        Exclude = []
        Replace = Map.empty
        CompilerOptions =
            {
                TypedArrays = false
                ClampByteArrays = false
                Language = Language.JavaScript
                Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                DebugMode = false
                OptimizeFSharpAst = false
                Verbosity = Verbosity.Verbose
                FileExtension = ".js"
                TriggeredByDependency = false
                NoReflection = false
            }
    }

let dummyPathResolver =
    { new PathResolver with
        member _.TryPrecompiledOutPath (_sourceDir, _relativePath) = None
        member _.GetOrAddDeduplicateTargetDir (_importDir, _addTargetDir) = ""
    }


type FableServer(sender : Stream, reader : Stream) as this =
    let rpc : JsonRpc = JsonRpc.Attach (sender, reader, this)

    let mailbox =
        MailboxProcessor.Start (fun inbox ->
            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    match msg with
                    | ProjectChanged (fsproj, replyChannel) ->
                        let projectOptions = CoolCatCracking.mkOptionsFromDesignTimeBuild fsproj ""
                        replyChannel.Reply projectOptions

                        let crackerResponse : CrackerResponse =
                            {
                                // TODO: update to sample
                                FableLibDir = @"C:\Users\nojaf\Projects\MyFableApp\fable_modules\fable-library.4.3.0"
                                FableModulesDir = @"C:\Users\nojaf\Projects\MyFableApp\fable_modules"
                                References = []
                                ProjectOptions = projectOptions
                                OutputType = OutputType.Library
                                TargetFramework = "net8.0"
                                PrecompiledInfo = None
                                CanReuseCompiledFiles = false
                            }

                        let checker = InteractiveChecker.Create (projectOptions)

                        let sourceReader =
                            Fable.Transforms.File.MakeSourceReader (
                                Array.map Fable.Transforms.File crackerResponse.ProjectOptions.SourceFiles
                            )
                            |> snd

                        let! compilers =
                            Fable.Compiler.CodeServices.mkCompilersForProject
                                sourceReader
                                checker
                                cliArgs
                                crackerResponse

                        return!
                            loop
                                {
                                    CrackerResponse = crackerResponse
                                    Checker = checker
                                    SourceReader = sourceReader
                                    Compilers = compilers
                                }

                    // TODO: this probably means the file was changed as well.
                    | CompileFile (fileName, replyChannel) ->
                        let fileName = Path.normalizePath fileName

                        match Map.tryFind fileName model.Compilers with
                        | None -> failwith $"File {fileName}"
                        | Some compiler ->
                            let! javascript, dependentFiles =
                                Fable.Compiler.CodeServices.compileFile
                                    model.SourceReader
                                    compiler
                                    dummyPathResolver
                                    (Path.ChangeExtension (fileName, ".js"))

                            replyChannel.Reply javascript
                            return! loop model
                    | Disconnect -> return ()
                }

            loop Unchecked.defaultof<Model>
        )

    interface IDisposable with
        member _.Dispose () = ()

    /// returns a hot task that resolves when the stream has terminated
    member this.WaitForClose = rpc.Completion

    [<JsonRpcMethod("fable/ping", UseSingleObjectParameterDeserialization = true)>]
    member _.Ping (p : PingPayload) : Task<string> =
        task { return "And dotnet will answer" }

    [<JsonRpcMethod("fable/init", UseSingleObjectParameterDeserialization = true)>]
    member _.Init (p : InitPayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p.Project, replyChannel)) }

    [<JsonRpcMethod("fable/compile", UseSingleObjectParameterDeserialization = true)>]
    member _.CompileFile (p : CompileFilePayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.CompileFile (p.FileName, replyChannel)) }

let input = Console.OpenStandardInput ()
let output = Console.OpenStandardOutput ()

let daemon =
    new FableServer (Console.OpenStandardOutput (), Console.OpenStandardInput ())

AppDomain.CurrentDomain.ProcessExit.Add (fun _ -> (daemon :> IDisposable).Dispose ())
daemon.WaitForClose.GetAwaiter().GetResult ()
exit 0