﻿open System
open System.IO
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization
open StreamJsonRpc
open Fable
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceCodeServices
open FSharp.Compiler.Diagnostics
open Fable.Compiler.ProjectCracker
open Fable.Compiler.Util
open Fable.Compiler
open Fable.Daemon

type Msg =
    | ProjectChanged of payload : ProjectChangedPayload * AsyncReplyChannel<ProjectChangedResult>
    | CompileFullProject of AsyncReplyChannel<FilesCompiledResult>
    | CompileFile of fileName : string * AsyncReplyChannel<FileChangedResult>
    | Disconnect

type Model =
    {
        ProjectCrackerResolver : ProjectCrackerResolver
        CliArgs : CliArgs
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        PathResolver : PathResolver
        TypeCheckProjectResult : TypeCheckProjectResult
    }

type PongResponse = { Message : string }

type TypeCheckedProjectData =
    {
        TypeCheckProjectResult : TypeCheckProjectResult
        CliArgs : CliArgs
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
    }

let tryTypeCheckProject
    (crackerResolver : ProjectCrackerResolver)
    (payload : ProjectChangedPayload)
    : Async<Result<TypeCheckedProjectData, string>>
    =
    async {
        try
            let cliArgs : CliArgs =
                {
                    ProjectFile = payload.Project
                    RootDir = Path.GetDirectoryName payload.Project
                    OutDir = None
                    IsWatch = false
                    Precompile = false
                    PrecompiledLib = None
                    PrintAst = false
                    FableLibraryPath = Some payload.FableLibrary
                    Configuration = payload.Configuration
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
                            // We keep using `.fs` for the compiled FSharp file, even though the contents will be JavaScript.
                            FileExtension = ".fs"
                            TriggeredByDependency = false
                            NoReflection = false
                        }
                    RunProcess = None
                    Verbosity = Verbosity.Normal
                }

            let crackerOptions = CrackerOptions (cliArgs, false)
            let crackerResponse = getFullProjectOpts crackerResolver crackerOptions
            let checker = InteractiveChecker.Create crackerResponse.ProjectOptions

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File crackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! typeCheckResult = CodeServices.typeCheckProject sourceReader checker cliArgs crackerResponse

            return
                Ok
                    {
                        TypeCheckProjectResult = typeCheckResult
                        CliArgs = cliArgs
                        Checker = checker
                        CrackerResponse = crackerResponse
                        SourceReader = sourceReader
                    }
        with ex ->
            return Error ex.Message
    }

type CompiledProjectData =
    {
        CompiledFSharpFiles : Map<string, string>
    }

let private mapRange (m : FSharp.Compiler.Text.range) =
    {
        StartLine = m.StartLine
        StartColumn = m.StartColumn
        EndLine = m.EndLine
        EndColumn = m.EndColumn
    }

let private mapDiagnostics (ds : FSharpDiagnostic array) =
    ds
    |> Array.map (fun d ->
        {
            ErrorNumberText = d.ErrorNumberText
            Message = d.Message
            Range = mapRange d.Range
            Severity = string d.Severity
            FileName = d.FileName
        }
    )

let tryCompileProject
    (pathResolver : PathResolver)
    (cliArgs : CliArgs)
    (crackerResponse : CrackerResponse)
    (typeCheckProjectResult : TypeCheckProjectResult)
    : Async<Result<CompiledProjectData, string>>
    =
    async {
        try
            let files =
                crackerResponse.ProjectOptions.SourceFiles
                |> Array.filter (fun sf -> not (sf.EndsWith (".fsi", StringComparison.Ordinal)))

            let! initialCompileResponse =
                CodeServices.compileMultipleFilesToJavaScript
                    pathResolver
                    cliArgs
                    crackerResponse
                    typeCheckProjectResult
                    files

            return
                Ok
                    {
                        CompiledFSharpFiles = initialCompileResponse.CompiledFiles
                    }
        with ex ->
            return Error ex.Message
    }

type CompiledFileData =
    {
        CompiledFiles : Map<string, string>
        Diagnostics : FSharpDiagnostic array
    }

let tryCompileFile (model : Model) (fileName : string) : Async<Result<CompiledFileData, string>> =
    async {
        try
            let fileName = Path.normalizePath fileName

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File model.CrackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! compiledFileResponse =
                Fable.Compiler.CodeServices.compileFileToJavaScript
                    sourceReader
                    model.Checker
                    model.PathResolver
                    model.CliArgs
                    model.CrackerResponse
                    fileName

            return
                Ok
                    {
                        CompiledFiles = compiledFileResponse.CompiledFiles
                        Diagnostics = compiledFileResponse.Diagnostics
                    }
        with ex ->
            return Error ex.Message
    }

type FableServer(sender : Stream, reader : Stream) as this =
    let jsonMessageFormatter = new SystemTextJsonFormatter ()

    do
        jsonMessageFormatter.JsonSerializerOptions <-
            let options =
                JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

            let jsonFSharpOptions =
                JsonFSharpOptions
                    .Default()
                    .WithUnionTagName("case")
                    .WithUnionFieldsName ("fields")

            options.Converters.Add (JsonUnionConverter jsonFSharpOptions)
            options

    let handler =
        new HeaderDelimitedMessageHandler (sender, reader, jsonMessageFormatter)

    let rpc : JsonRpc = new JsonRpc (handler, this)
    do rpc.StartListening ()

    let mailbox =
        MailboxProcessor.Start (fun inbox ->
            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    match msg with
                    | ProjectChanged (payload, replyChannel) ->
                        let! result = tryTypeCheckProject model.ProjectCrackerResolver payload

                        match result with
                        | Error error ->
                            replyChannel.Reply (ProjectChangedResult.Error error)
                            return! loop model
                        | Ok result ->

                        replyChannel.Reply (
                            ProjectChangedResult.Success (
                                result.CrackerResponse.ProjectOptions,
                                mapDiagnostics result.TypeCheckProjectResult.ProjectCheckResults.Diagnostics
                            )
                        )

                        return!
                            loop
                                { model with
                                    CliArgs = result.CliArgs
                                    Checker = result.Checker
                                    CrackerResponse = result.CrackerResponse
                                    SourceReader = result.SourceReader
                                    TypeCheckProjectResult = result.TypeCheckProjectResult
                                }

                    | CompileFullProject replyChannel ->
                        let dummyPathResolver =
                            { new PathResolver with
                                member _.TryPrecompiledOutPath (_sourceDir, _relativePath) = None
                                member _.GetOrAddDeduplicateTargetDir (importDir, addTargetDir) = importDir
                            }

                        let! result =
                            tryCompileProject
                                dummyPathResolver
                                model.CliArgs
                                model.CrackerResponse
                                model.TypeCheckProjectResult

                        match result with
                        | Error error ->
                            replyChannel.Reply (FilesCompiledResult.Error error)
                            return! loop model
                        | Ok result ->
                            replyChannel.Reply (FilesCompiledResult.Success result.CompiledFSharpFiles)

                            return!
                                loop
                                    { model with
                                        PathResolver = dummyPathResolver
                                    }

                    // TODO: this probably means the file was changed as well.
                    | CompileFile (fileName, replyChannel) ->
                        let! result = tryCompileFile model fileName

                        match result with
                        | Error error -> replyChannel.Reply (FileChangedResult.Error error)
                        | Ok result ->
                            replyChannel.Reply (
                                FileChangedResult.Success (result.CompiledFiles, mapDiagnostics result.Diagnostics)
                            )

                        return! loop model
                    | Disconnect -> return ()
                }

            loop
                {
                    ProjectCrackerResolver = CoolCatCracking.CoolCatResolver ()
                    CliArgs = Unchecked.defaultof<CliArgs>
                    Checker = Unchecked.defaultof<InteractiveChecker>
                    CrackerResponse = Unchecked.defaultof<CrackerResponse>
                    SourceReader = Unchecked.defaultof<SourceReader>
                    PathResolver = Unchecked.defaultof<PathResolver>
                    TypeCheckProjectResult = Unchecked.defaultof<TypeCheckProjectResult>
                }
        )

    // log or something.
    let subscription = mailbox.Error.Subscribe (fun evt -> ())

    interface IDisposable with
        member _.Dispose () =
            if not (isNull subscription) then
                subscription.Dispose ()

            ()

    /// returns a hot task that resolves when the stream has terminated
    member this.WaitForClose = rpc.Completion

    [<JsonRpcMethod("fable/ping", UseSingleObjectParameterDeserialization = true)>]
    member _.Ping (_p : PingPayload) : Task<PongResponse> =
        task { return { Message = "And dotnet will answer" } }

    [<JsonRpcMethod("fable/project-changed", UseSingleObjectParameterDeserialization = true)>]
    member _.ProjectChanged (p : ProjectChangedPayload) =
        task { return! mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel)) }

    [<JsonRpcMethod("fable/initial-compile", UseSingleObjectParameterDeserialization = true)>]
    member _.InitialCompile () =
        task { return! mailbox.PostAndAsyncReply Msg.CompileFullProject }

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
