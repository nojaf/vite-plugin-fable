open System
open System.IO
open System.Threading.Tasks
open Fable
open Newtonsoft.Json
open Newtonsoft.Json.Serialization
open StreamJsonRpc
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceCodeServices
open Fable.Compiler.Service.ProjectCracker
open Fable.Compiler.Service.Util
open Fable.Daemon

type Msg =
    | ProjectChanged of payload : ProjectChangedPayload * AsyncReplyChannel<ProjectChangedResult>
    | CompileFile of fileName : string * AsyncReplyChannel<FileChangedResult>
    | Disconnect

type Model =
    {
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
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

type PongResponse = { Message : string }

type FableServer(sender : Stream, reader : Stream) as this =
    let rpc : JsonRpc = JsonRpc.Attach (sender, reader, this)

    let mailbox =
        MailboxProcessor.Start (fun inbox ->
            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    match msg with
                    | ProjectChanged (payload, replyChannel) ->
                        let projectOptions = CoolCatCracking.mkOptionsFromDesignTimeBuild payload.Project ""

                        let crackerResponse : CrackerResponse =
                            {
                                FableLibDir = payload.FableLibrary
                                // TODO: update to sample
                                FableModulesDir =
                                    @"C:\Users\nojaf\Projects\vite-plugin-fable\sample-project\fable_modules"
                                References = []
                                ProjectOptions = projectOptions
                                OutputType = OutputType.Library
                                TargetFramework = "net8.0"
                                PrecompiledInfo = None
                                CanReuseCompiledFiles = false
                            }

                        let checker = InteractiveChecker.Create projectOptions

                        let sourceReader =
                            Fable.Compiler.File.MakeSourceReader (
                                Array.map Fable.Compiler.File crackerResponse.ProjectOptions.SourceFiles
                            )
                            |> snd

                        let! initialCompiledFiles =
                            Fable.Compiler.CodeServices.compileProjectToJavaScript
                                sourceReader
                                checker
                                dummyPathResolver
                                cliArgs
                                crackerResponse

                        replyChannel.Reply
                            {
                                ProjectOptions = projectOptions
                                CompiledFSharpFiles = initialCompiledFiles
                            }

                        return!
                            loop
                                {
                                    CrackerResponse = crackerResponse
                                    Checker = checker
                                    SourceReader = sourceReader
                                }

                    // TODO: this probably means the file was changed as well.
                    | CompileFile (fileName, replyChannel) ->
                        let fileName = Path.normalizePath fileName

                        let! compiledFiles =
                            Fable.Compiler.CodeServices.compileFileToJavaScript
                                model.SourceReader
                                model.Checker
                                dummyPathResolver
                                cliArgs
                                model.CrackerResponse
                                fileName

                        replyChannel.Reply { CompiledFSharpFiles = compiledFiles }
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
    member _.Ping (_p : PingPayload) : Task<PongResponse> =
        task { return { Message = "And dotnet will answer" } }

    [<JsonRpcMethod("fable/init", UseSingleObjectParameterDeserialization = true)>]
    member _.Init (p : ProjectChangedPayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel)) }

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
