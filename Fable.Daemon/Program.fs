open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
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

/// Input for every getFullProjectOpts
/// Should be reused for subsequent type checks.
type CrackerInput =
    {
        CliArgs : CliArgs
        /// Reuse the cracker options in future design time builds
        CrackerOptions : CrackerOptions
    }

type Model =
    {
        CoolCatResolver : CoolCatResolver
        Checker : InteractiveChecker
        CrackerInput : CrackerInput option
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        PathResolver : PathResolver
        TypeCheckProjectResult : TypeCheckProjectResult
    }

let logger : ILogger =
    let envVar = Environment.GetEnvironmentVariable "VITE_PLUGIN_FABLE_DEBUG"

    if not (String.IsNullOrWhiteSpace envVar) && not (envVar = "0") then
        Debug.InMemoryLogger ()
    else
        NullLogger.Instance

// Set Fable logger
Log.setLogger Verbosity.Verbose logger

let timeAsync f =
    async {
        let sw = Stopwatch.StartNew ()
        let! result = f
        sw.Stop ()
        return result, sw.Elapsed
    }

type TypeCheckedProjectData =
    {
        TypeCheckProjectResult : TypeCheckProjectResult
        CrackerInput : CrackerInput
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        /// An array of files that influence the design time build
        /// If any of these change, the plugin should respond accordingly.
        DependentFiles : FullPath array
    }

let tryTypeCheckProject
    (model : Model)
    (payload : ProjectChangedPayload)
    : Async<Result<TypeCheckedProjectData, string>>
    =
    async {
        try
            /// Project file will be in the Vite normalized format
            let projectFile = Path.GetFullPath payload.Project
            logger.LogDebug ("start tryTypeCheckProject for {projectFile}", projectFile)

            let cliArgs, crackerOptions =
                match model.CrackerInput with
                | Some {
                           CliArgs = cliArgs
                           CrackerOptions = crackerOptions
                       } -> cliArgs, crackerOptions
                | None ->

                let cliArgs : CliArgs =
                    {
                        ProjectFile = projectFile
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
                        Exclude = List.ofArray payload.Exclude
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
                                NoReflection = payload.NoReflection
                            }
                        RunProcess = None
                        Verbosity = Verbosity.Verbose
                    }

                cliArgs, CrackerOptions (cliArgs, true)

            let crackerResponse = getFullProjectOpts model.CoolCatResolver crackerOptions
            logger.LogDebug ("CrackerResponse: {crackerResponse}", crackerResponse)
            let checker = InteractiveChecker.Create crackerResponse.ProjectOptions

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File crackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! typeCheckResult, typeCheckTime =
                timeAsync (CodeServices.typeCheckProject sourceReader checker cliArgs crackerResponse)

            logger.LogDebug ("Typechecking {projectFile} took {elapsed}", projectFile, typeCheckTime)

            let dependentFiles =
                model.CoolCatResolver.MSBuildProjectFiles projectFile
                |> List.map (fun fi -> fi.FullName)
                |> List.toArray

            return
                Ok
                    {
                        TypeCheckProjectResult = typeCheckResult
                        CrackerInput =
                            Option.defaultValue
                                {
                                    CliArgs = cliArgs
                                    CrackerOptions = crackerOptions
                                }
                                model.CrackerInput
                        Checker = checker
                        CrackerResponse = crackerResponse
                        SourceReader = sourceReader
                        DependentFiles = dependentFiles
                    }
        with ex ->
            logger.LogCritical ("tryTypeCheckProject threw exception {ex}", ex)
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

let tryCompileProject (pathResolver : PathResolver) (model : Model) : Async<Result<CompiledProjectData, string>> =
    async {
        try
            let cachedFableModuleFiles =
                model.CoolCatResolver.TryGetCachedFableModuleFiles model.CrackerResponse.ProjectOptions.ProjectFileName

            let files =
                let cachedFiles = cachedFableModuleFiles.Keys |> Set.ofSeq

                model.CrackerResponse.ProjectOptions.SourceFiles
                |> Array.filter (fun sf ->
                    not (sf.EndsWith (".fsi", StringComparison.Ordinal))
                    && not (cachedFiles.Contains sf)
                )

            match model.CrackerInput with
            | None ->
                logger.LogCritical "tryCompileProject is entered without CrackerInput"
                return raise (exn "tryCompileProject is entered without CrackerInput")
            | Some { CliArgs = cliArgs } ->

            let! initialCompileResponse =
                CodeServices.compileMultipleFilesToJavaScript
                    pathResolver
                    cliArgs
                    model.CrackerResponse
                    model.TypeCheckProjectResult
                    files

            if cachedFableModuleFiles.IsEmpty then
                let fableModuleFiles =
                    initialCompileResponse.CompiledFiles
                    |> Map.filter (fun key _value -> key.Contains "fable_modules")

                model.CoolCatResolver.WriteCachedFableModuleFiles
                    model.CrackerResponse.ProjectOptions.ProjectFileName
                    fableModuleFiles

            let compiledFiles =
                (initialCompileResponse.CompiledFiles, cachedFableModuleFiles)
                ||> Map.fold (fun state key value -> Map.add key value state)

            return Ok { CompiledFSharpFiles = compiledFiles }
        with ex ->
            logger.LogCritical ("tryCompileProject threw exception {ex}", ex)
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
            logger.LogDebug ("tryCompileFile {fileName}", fileName)

            match model.CrackerInput with
            | None ->
                logger.LogCritical "tryCompileFile is entered without CrackerInput"
                return raise (exn "tryCompileFile is entered without CrackerInput")
            | Some { CliArgs = cliArgs } ->

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
                    cliArgs
                    model.CrackerResponse
                    fileName

            return
                Ok
                    {
                        CompiledFiles = compiledFileResponse.CompiledFiles
                        Diagnostics = compiledFileResponse.Diagnostics
                    }
        with ex ->
            logger.LogCritical ("tryCompileFile threw exception {ex}", ex)
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

    let cts = new CancellationTokenSource ()

    do
        match logger with
        | :? Debug.InMemoryLogger as logger ->
            let server = Debug.startWebserver logger cts.Token
            Async.Start (server, cts.Token)
        | _ -> ()

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
                        let! result = tryTypeCheckProject model payload

                        match result with
                        | Error error ->
                            replyChannel.Reply (ProjectChangedResult.Error error)
                            return! loop model
                        | Ok result ->

                        replyChannel.Reply (
                            ProjectChangedResult.Success (
                                result.CrackerResponse.ProjectOptions.SourceFiles,
                                mapDiagnostics result.TypeCheckProjectResult.ProjectCheckResults.Diagnostics,
                                result.DependentFiles
                            )
                        )

                        return!
                            loop
                                { model with
                                    CrackerInput = Some result.CrackerInput
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

                        let! result = tryCompileProject dummyPathResolver model


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
                    CoolCatResolver = CoolCatResolver logger
                    Checker = Unchecked.defaultof<InteractiveChecker>
                    CrackerResponse = Unchecked.defaultof<CrackerResponse>
                    SourceReader = Unchecked.defaultof<SourceReader>
                    PathResolver = Unchecked.defaultof<PathResolver>
                    TypeCheckProjectResult = Unchecked.defaultof<TypeCheckProjectResult>
                    CrackerInput = None
                }
        )

    // log or something.
    let subscription = mailbox.Error.Subscribe (fun evt -> ())

    interface IDisposable with
        member _.Dispose () =
            if not (isNull subscription) then
                subscription.Dispose ()

            if not cts.IsCancellationRequested then
                cts.Cancel ()

            ()

    /// returns a hot task that resolves when the stream has terminated
    member this.WaitForClose = rpc.Completion

    [<JsonRpcMethod("fable/project-changed", UseSingleObjectParameterDeserialization = true)>]
    member _.ProjectChanged (p : ProjectChangedPayload) : Task<ProjectChangedResult> =
        task {
            logger.LogDebug ("enter \"fable/project-changed\" {p}", p)
            let! response = mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel))
            logger.LogDebug ("exit \"fable/project-changed\" {response}", response)
            return response
        }

    [<JsonRpcMethod("fable/initial-compile", UseSingleObjectParameterDeserialization = true)>]
    member _.InitialCompile () : Task<FilesCompiledResult> =
        task {
            logger.LogDebug "enter \"fable/initial-compile\""
            let! response = mailbox.PostAndAsyncReply Msg.CompileFullProject

            let logResponse =
                match response with
                | FilesCompiledResult.Error e -> box e
                | FilesCompiledResult.Success result -> result.Keys |> String.concat "\n" |> sprintf "\n%s" |> box

            logger.LogDebug ("exit \"fable/initial-compile\" with {logResponse}", logResponse)
            return response
        }

    [<JsonRpcMethod("fable/compile", UseSingleObjectParameterDeserialization = true)>]
    member _.CompileFile (p : CompileFilePayload) : Task<FileChangedResult> =
        task {
            logger.LogDebug ("enter \"fable/compile\" with {p}", p)
            let! response = mailbox.PostAndAsyncReply (fun replyChannel -> Msg.CompileFile (p.FileName, replyChannel))

            let logResponse =
                match response with
                | FileChangedResult.Error e -> box e
                | FileChangedResult.Success (result, diagnostics) ->
                    let keys = result.Keys |> String.concat "\n" |> sprintf "\n%s"
                    box (keys, diagnostics)

            logger.LogDebug ("exit \"fable/compile\" with {p}", logResponse)
            return response
        }

let input = Console.OpenStandardInput ()
let output = Console.OpenStandardOutput ()

let daemon =
    new FableServer (Console.OpenStandardOutput (), Console.OpenStandardInput ())

AppDomain.CurrentDomain.ProcessExit.Add (fun _ -> (daemon :> IDisposable).Dispose ())
daemon.WaitForClose.GetAwaiter().GetResult ()
exit 0
