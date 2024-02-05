module Fable.Daemon.CoolCatCracking

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Collections.Concurrent
open Thoth.Json.Core
open Thoth.Json.SystemTextJson
open Fable
open Fable.Compiler.ProjectCracker

let fsharpFiles = set [| ".fs" ; ".fsi" ; ".fsx" |]

let isFSharpFile (file : string) =
    Set.exists (fun (ext : string) -> file.EndsWith (ext, StringComparison.Ordinal)) fsharpFiles

/// Transform F# files into full paths
let private mkOptions (projectFile : FileInfo) (compilerArgs : string array) : string array =
    compilerArgs
    |> Array.map (fun (line : string) ->
        if not (isFSharpFile line) then
            line
        else
            Path.Combine (projectFile.DirectoryName, line) |> Path.GetFullPath
    )

type FullPath = string

let dotnet_msbuild (fsproj : FullPath) (args : string) : Async<string> =
    backgroundTask {
        let psi = ProcessStartInfo "dotnet"
        let pwd = Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
        psi.WorkingDirectory <- pwd
        psi.Arguments <- $"msbuild \"%s{fsproj}\" %s{args}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use ps = new Process ()
        ps.StartInfo <- psi
        ps.Start () |> ignore
        let output = ps.StandardOutput.ReadToEnd ()
        let error = ps.StandardError.ReadToEnd ()
        do! ps.WaitForExitAsync ()

        if not (String.IsNullOrWhiteSpace error) then
            failwithf $"In %s{pwd}:\ndotnet msbuild \"%s{fsproj}\" %s{args} failed with\n%s{error}"

        return output.Trim ()
    }
    |> Async.AwaitTask

module Caching =
    [<Literal>]
    let CacheFileNameSuffix = ".vite-plugin-fable-cache.json"

    type Hash = string

    type CacheFileContent =
        {
            MainFsproj : Hash
            DependentFiles : Map<FullPath, Hash>
            Defines : Set<string>
            ProjectOptionsResponse : ProjectOptionsResponse
        }

    let cacheDecoder =
        Decode.object (fun get ->
            let mainFsproj = get.Required.Field "mainFsproj" Decode.string

            let dependentFiles =
                get.Required.Field "dependentFiles" (Decode.keyValuePairs Decode.string)
                |> Map.ofList

            let defines =
                get.Required.Field "defines" (Decode.array Decode.string) |> Set.ofArray

            let projectOptions =
                get.Required.Field "projectOptions" (Decode.array Decode.string)

            let projectReferences =
                get.Required.Field "projectReferences" (Decode.array Decode.string)

            let outputType = get.Optional.Field "outputType" Decode.string
            let targetFramework = get.Optional.Field "targetFramework" Decode.string

            {
                MainFsproj = mainFsproj
                DependentFiles = dependentFiles
                Defines = defines
                ProjectOptionsResponse =
                    {
                        ProjectOptions = projectOptions
                        ProjectReferences = projectReferences
                        OutputType = outputType
                        TargetFramework = targetFramework
                    }
            }
        )

    /// Calculates the SHA256 hash of the given file.
    type FileInfo with
        member this.Hash : string =
            use sha256 = System.Security.Cryptography.SHA256.Create ()
            use stream = File.OpenRead this.FullName
            let hash = sha256.ComputeHash stream
            BitConverter.ToString(hash).Replace ("-", "")

    [<RequireQualifiedAccess>]
    type InvalidCacheReason =
        | FileDoesNotExist of cacheFile : FileInfo
        | CouldNotDecodeCachedArguments of error : string
        | MainFsprojChanged
        | DefinesMismatch of cachedDefines : Set<string> * currentDefines : Set<string>
        | DependentFileCountDoesNotMatch of cachedCount : int * currentCount : int
        | DependentFileHashMismatch of file : FileInfo

    type CacheKey =
        {
            /// Input fsproj project.
            MainFsproj : FileInfo
            /// This is the file that contains the cached information.
            /// Initially it doesn't exist and can only be checked in subsequent runs.
            CacheFile : FileInfo
            /// All the files that can influence the MSBuild evaluation.
            /// This typically is the
            DependentFiles : FileInfo list
            /// Contains both the user defined configurations (via Vite plugin options)
            Defines : Set<string>
            /// Configuration
            Configuration : string
        }

        /// Verify is the cached key for the project exists and is still valid.
        member x.CanReusCachedProjectOptions () : Result<ProjectOptionsResponse, InvalidCacheReason> =
            if not x.CacheFile.Exists then
                Error (InvalidCacheReason.FileDoesNotExist x.CacheFile)
            else

            let cacheContent = File.ReadAllText x.CacheFile.FullName

            match Decode.fromString cacheDecoder cacheContent with
            | Error error -> Error (InvalidCacheReason.CouldNotDecodeCachedArguments error)
            | Ok cacheContent ->

            if x.MainFsproj.Hash <> cacheContent.MainFsproj then
                Error InvalidCacheReason.MainFsprojChanged
            elif x.Defines <> cacheContent.Defines then
                Error (InvalidCacheReason.DefinesMismatch (cacheContent.Defines, x.Defines))
            elif x.DependentFiles.Length <> cacheContent.DependentFiles.Count then
                Error (
                    InvalidCacheReason.DependentFileCountDoesNotMatch (
                        cacheContent.DependentFiles.Count,
                        x.DependentFiles.Length
                    )
                )
            else

            // Verify if each dependent files was found in the cached data and if the hashes still match.
            let mismatchedFile =
                x.DependentFiles
                |> List.tryFind (fun df ->
                    match Map.tryFind df.FullName cacheContent.DependentFiles with
                    | None -> true
                    | Some v -> df.Hash <> v
                )

            match mismatchedFile with
            | None -> Ok cacheContent.ProjectOptionsResponse
            | Some mmf -> Error (InvalidCacheReason.DependentFileHashMismatch mmf)

        /// Save the compiler arguments results from the design time build to the intermediate folder.
        member x.Write (response : ProjectOptionsResponse) =
            let json =
                let dependentFiles =
                    x.DependentFiles
                    |> List.map (fun df -> df.FullName, Encode.string df.Hash)
                    |> Encode.object

                Encode.object
                    [
                        "mainFsproj", Encode.string x.MainFsproj.Hash
                        "dependentFiles", dependentFiles
                        "defines", x.Defines |> Seq.map Encode.string |> Seq.toArray |> Encode.array
                        "projectOptions", response.ProjectOptions |> Array.map Encode.string |> Encode.array
                        "projectReferences", response.ProjectReferences |> Array.map Encode.string |> Encode.array
                        "outputType", Encode.option Encode.string response.OutputType
                        "targetFramework", Encode.option Encode.string response.TargetFramework
                    ]

            File.WriteAllText (x.CacheFile.FullName, Encode.toString json)

    let cacheKeyDecoder (options : CrackerOptions) (fsproj : FileInfo) : Decoder<CacheKey> =
        Decode.object (fun get ->
            let paths =
                let value = get.Required.At [ "Properties" ; "MSBuildAllProjects" ] Decode.string

                value.Split (';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun path ->
                    let fi = FileInfo path

                    if not fi.Exists then None else Some fi
                )

            let intermediateOutputPath =
                let v =
                    get.Required.At [ "Properties" ; "BaseIntermediateOutputPath" ] Decode.string

                let v = v.TrimEnd '\\'
                Path.Combine (fsproj.DirectoryName, v) |> Path.GetFullPath

            let nugetGProps =
                let gPropFile =
                    Path.Combine (intermediateOutputPath, $"%s{fsproj.Name}.nuget.g.props")
                    |> FileInfo

                if not gPropFile.Exists then [] else [ gPropFile ]

            let cacheFile =
                FileInfo (
                    System.IO.Path.Combine (
                        intermediateOutputPath,
                        options.Configuration,
                        $"{fsproj.Name}%s{CacheFileNameSuffix}"
                    )
                )

            {
                MainFsproj = fsproj
                CacheFile = cacheFile
                DependentFiles = [ yield! paths ; yield! nugetGProps ]
                Defines = Set.ofList options.FableOptions.Define
                Configuration = options.Configuration
            }
        )

    /// Generate the caching key information for the design time build of the incoming fsproj file.
    let mkProjectCacheKey (options : CrackerOptions) (fsproj : FileInfo) : Async<Result<CacheKey, string>> =
        async {
            if not fsproj.Exists then
                raise (ArgumentException ($"%s{fsproj.FullName} does not exists", nameof fsproj))

            if String.IsNullOrWhiteSpace options.Configuration then
                raise (
                    ArgumentException (
                        "options.Configuration cannot be null or whitespace",
                        nameof options.Configuration
                    )
                )

            let! json =
                dotnet_msbuild
                    fsproj.FullName
                    "--getProperty:MSBuildAllProjects --getProperty:BaseIntermediateOutputPath"

            return Decode.fromString (cacheKeyDecoder options fsproj) json
        }

let private identityDecoder =
    Decode.object (fun get -> get.Required.Field "Identity" Decode.string)

/// Perform a design time build using the `dotnet msbuild` cli invocation.
let mkOptionsFromDesignTimeBuildAux (fsproj : FileInfo) (options : CrackerOptions) : Async<ProjectOptionsResponse> =
    async {
        let! targetFrameworkJson =
            let configuration =
                if String.IsNullOrWhiteSpace options.Configuration then
                    ""
                else
                    $"/p:Configuration=%s{options.Configuration}"

            dotnet_msbuild
                fsproj.FullName
                $"{configuration} --getProperty:TargetFrameworks --getProperty:TargetFramework --getProperty:DefineConstants"

        // To perform a design time build we need to target an exact single TargetFramework
        // There is a slight chance that the fsproj uses <TargetFrameworks>net8.0</TargetFrameworks>
        // We need to take this into account.
        let defineConstants, targetFramework =
            let decoder =
                Decode.object (fun get ->
                    get.Required.At [ "Properties" ; "DefineConstants" ] Decode.string,
                    get.Required.At [ "Properties" ; "TargetFramework" ] Decode.string,
                    get.Required.At [ "Properties" ; "TargetFrameworks" ] Decode.string
                )

            match Decode.fromString decoder targetFrameworkJson with
            | Error e -> failwithf $"Could not decode target framework json, %A{e}"
            | Ok (defineConstants, tf, tfs) ->

            let defineConstants =
                defineConstants.Split ';'
                |> Array.filter (fun c -> c <> "DEBUG" || c <> "RELEASE")

            if not (String.IsNullOrWhiteSpace tf) then
                defineConstants, tf
            else
                defineConstants, tfs.Split ';' |> Array.head

        // TRACE is typically present for fsproj projects
        let defines =
            [
                "TRACE"
                if not (String.IsNullOrWhiteSpace options.Configuration) then
                    options.Configuration.ToUpper ()
                yield! defineConstants
                yield! options.FableOptions.Define
            ]

            |> List.map (fun s -> s.Trim ())
            // Escaped `;`
            |> String.concat "%3B"

        // When CoreCompile does not need a rebuild, MSBuild will skip that target and thus will not populate the FscCommandLineArgs items.
        // To overcome this we want to force a design time build, using the NonExistentFile property helps prevent a cache hit.
        let nonExistentFile = Path.Combine ("__NonExistentSubDir__", "__NonExistentFile__")

        let properties =
            [
                "/p:VitePlugin=True"
                if not (String.IsNullOrWhiteSpace options.Configuration) then
                    $"/p:Configuration=%s{options.Configuration}"
                if not (String.IsNullOrWhiteSpace defines) then
                    $"/p:DefineConstants=\"%s{defines}\""
                $"/p:TargetFramework=%s{targetFramework}"
                "/p:DesignTimeBuild=True"
                "/p:SkipCompilerExecution=True"
                // This will populate FscCommandLineArgs
                "/p:ProvideCommandLineArgs=True"
                // See https://github.com/NuGet/Home/issues/13046
                "/p:RestoreUseStaticGraphEvaluation=False"
                // Avoid restoring with an existing lock file
                "/p:RestoreLockedMode=false"
                "/p:RestorePackagesWithLockFile=false"
                // We trick NuGet into believing there is no lock file create, if it does exist it will try and create it.
                " /p:NuGetLockFilePath=VitePlugin.lock"
                // Avoid skipping the CoreCompile target via this property.
                $"/p:NonExistentFile=\"%s{nonExistentFile}\""
            ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> String.concat " "

        // We do not specify the Restore target itself, the `/restore` flag will take care of this.
        // Imagine with me for a moment how MSBuild works for a given project:
        //
        // it opens the project file
        // it reads and loads the MSBuild SDKs specified in the project file
        // it follows any Imports in those props/targets
        // it then executes the targets involved
        // this is why the /restore flag exists - this tells MSBuild-the-engine to do an entirely separate call to /t:Restore before whatever you specified,
        // so that the targets you specified run against a fully-correct local environment with all the props/targets files
        let targets =
            "ResolveAssemblyReferencesDesignTime,ResolveProjectReferencesDesignTime,ResolvePackageDependenciesDesignTime,FindReferenceAssembliesForReferences,_GenerateCompileDependencyCache,_ComputeNonExistentFileProperty,BeforeBuild,BeforeCompile,CoreCompile"

        // NU1608: Detected package version outside of dependency constraint, see https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1608
        let arguments =
            $"/restore /t:%s{targets} %s{properties}  -warnAsMessage:NU1608 --getItem:FscCommandLineArgs --getItem:ProjectReference --getProperty:OutputType"

        let! json = dotnet_msbuild fsproj.FullName arguments

        let decoder =
            Decode.object (fun get ->
                let options =
                    get.Required.At [ "Items" ; "FscCommandLineArgs" ] (Decode.array identityDecoder)

                let projectReferences =
                    get.Required.At [ "Items" ; "ProjectReference" ] (Decode.array identityDecoder)

                let outputType = get.Required.At [ "Properties" ; "OutputType" ] Decode.string
                options, projectReferences, outputType
            )

        match Decode.fromString decoder json with
        | Error e -> return failwithf $"Could not decode the design time build json, %A{e}"
        | Ok (options, projectReferences, outputType) ->

        if Array.isEmpty options then
            return
                failwithf
                    $"Design time build for %s{fsproj.FullName} failed. CoreCompile was most likely skipped. `dotnet clean` might help here.\ndotnet msbuild %s{fsproj.FullName} %s{arguments}"
        else

        let options = mkOptions fsproj options

        let projectReferences =
            projectReferences
            |> Seq.map (fun relativePath -> Path.Combine (fsproj.DirectoryName, relativePath) |> Path.GetFullPath)
            |> Seq.toArray

        return
            {
                ProjectOptions = options
                ProjectReferences = projectReferences
                OutputType = Some outputType
                TargetFramework = Some targetFramework
            }
    }

/// Crack the fsproj using the `dotnet msbuild --getProperty --getItem` command
/// See https://devblogs.microsoft.com/dotnet/announcing-dotnet-8-rc2/#msbuild-simple-cli-based-project-evaluation
type CoolCatResolver() =
    let cached = ConcurrentDictionary<FullPath, Caching.CacheKey> ()

    interface ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            async {
                let fsproj = FileInfo projectFile

                if not fsproj.Exists then
                    invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

                let! currentCacheKey =
                    async {
                        if cached.ContainsKey fsproj.FullName then
                            return cached.[fsproj.FullName]
                        else
                            match! Caching.mkProjectCacheKey options fsproj with
                            | Error error ->
                                return failwithf $"Could not construct cache key for %s{projectFile}, %A{error}"
                            | Ok cacheKey -> return cacheKey
                    }

                match currentCacheKey.CanReusCachedProjectOptions () with
                | Ok projectOptionsResponse ->
                    // The sweet spot, nothing changed and we can skip the design time build
                    return projectOptionsResponse
                | Error reason ->
                    // Delete the current cache file if it is no longer valid.
                    match reason with
                    | Caching.InvalidCacheReason.CouldNotDecodeCachedArguments _
                    | Caching.InvalidCacheReason.MainFsprojChanged
                    | Caching.InvalidCacheReason.DefinesMismatch _
                    | Caching.InvalidCacheReason.DependentFileCountDoesNotMatch _
                    | Caching.InvalidCacheReason.DependentFileHashMismatch _ ->
                        try
                            File.Delete currentCacheKey.CacheFile.FullName
                        finally
                            ()
                    | Caching.InvalidCacheReason.FileDoesNotExist _ -> ()

                    // Perform design time build and cache result
                    let! result = mkOptionsFromDesignTimeBuildAux fsproj options
                    currentCacheKey.Write result

                    cached.AddOrUpdate (fsproj.FullName, (fun _ -> currentCacheKey), (fun _ _ -> currentCacheKey))
                    |> ignore

                    return result
            }
            |> Async.RunSynchronously
