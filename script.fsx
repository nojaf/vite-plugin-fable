#r "./bin/Thoth.Json.Core.dll"
#r "./bin/Fable.AST.dll"
#r "./bin/Fable.Compiler.dll"
#load "./Fable.Daemon/Thoth/Decode.fs"
#load "./Fable.Daemon/Thoth/Encode.fs"
#load "./Fable.Daemon/CoolCatCracking.fs"

open System
open System.IO
open Thoth.Json.Core
open Thoth.Json.SystemTextJson
open Fable.Compiler.Util
open Fable.Compiler.ProjectCracker
open Fable.Daemon.CoolCatCracking

[<Literal>]
let CacheFileNameSuffix = ".vite-plugin-fable-cache.json"

type Hash = string

type CacheFileContent =
    {
        MainFsproj : Hash
        DependentFiles : Map<FullPath, Hash>
        Defines : Set<string>
        CompilerArguments : string array
    }

let cacheDecoder =
    Decode.object (fun get ->
        let mainFsproj = get.Required.Field "mainFsproj" Decode.string

        let dependentFiles =
            get.Required.Field "dependentFiles" (Decode.keyValuePairs Decode.string)
            |> Map.ofList

        let defines =
            get.Required.Field "defines" (Decode.array Decode.string) |> Set.ofArray

        let compilerArguments =
            get.Required.Field "compilerArguments" (Decode.array Decode.string)

        {
            MainFsproj = mainFsproj
            DependentFiles = dependentFiles
            Defines = defines
            CompilerArguments = compilerArguments
        }
    )

/// Calculates the SHA256 hash of the given file.
type FileInfo with
    member this.Hash : string =
        use sha256 = System.Security.Cryptography.SHA256.Create ()
        use stream = File.OpenRead this.FullName
        let hash = sha256.ComputeHash stream
        BitConverter.ToString(hash).Replace ("-", "")

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
    member x.Exists () : bool =
        if not x.CacheFile.Exists then
            false
        else

        let cacheContent = File.ReadAllText x.CacheFile.FullName

        match Decode.fromString cacheDecoder cacheContent with
        | Error _error ->
            // Maybe log in the future...
            false
        | Ok cacheContent ->

        if x.MainFsproj.Hash <> cacheContent.MainFsproj then
            false
        elif x.Defines <> cacheContent.Defines then
            false
        elif x.DependentFiles.Length <> cacheContent.DependentFiles.Count then
            false
        elif
            // Verify if each dependent files was found in the cached data and if the hashes still match.
            x.DependentFiles
            |> List.exists (fun df ->
                match Map.tryFind df.FullName cacheContent.DependentFiles with
                | None -> false
                | Some v -> df.Hash = v
            )
        then
            false
        else
            true

    /// Save the compiler arguments results from the design time build to the intermediate folder.
    member x.Write (compilerArguments : string array) =
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
                    "compilerArguments", compilerArguments |> Array.map Encode.string |> Encode.array
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
                Path.Combine (intermediateOutputPath, options.Configuration, $"{fsproj.Name}%s{CacheFileNameSuffix}")
            )

        {
            MainFsproj = fsproj
            CacheFile = cacheFile
            DependentFiles = [ yield fsproj ; yield! paths ; yield! nugetGProps ]
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
                ArgumentException ("options.Configuration cannot be null or whitespace", nameof options.Configuration)
            )

        let! json =
            dotnet_msbuild fsproj.FullName "--getProperty:MSBuildAllProjects --getProperty:BaseIntermediateOutputPath"

        return Decode.fromString (cacheKeyDecoder options fsproj) json
    }

// Execute
let fsproj =
    Path.Combine (__SOURCE_DIRECTORY__, "sample-project/App.fsproj") |> FileInfo

let cliArgs : CliArgs =
    {
        ProjectFile = fsproj.FullName
        RootDir = __SOURCE_DIRECTORY__
        OutDir = None
        IsWatch = false
        Precompile = false
        PrecompiledLib = None
        PrintAst = false
        FableLibraryPath = Some (Path.Combine (__SOURCE_DIRECTORY__, "sample-project/node_modules/fable-library"))
        Configuration = "Debug"
        NoRestore = false
        NoCache = true
        NoParallelTypeCheck = false
        SourceMaps = false
        SourceMapsRoot = None
        Exclude = []
        Replace = Map.empty
        RunProcess = None
        CompilerOptions =
            {
                TypedArrays = true
                ClampByteArrays = false
                Language = Fable.Language.JavaScript
                Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                DebugMode = true
                OptimizeFSharpAst = false
                Verbosity = Fable.Verbosity.Verbose
                FileExtension = ".fs"
                TriggeredByDependency = false
                NoReflection = false
            }
        Verbosity = Fable.Verbosity.Verbose
    }

let options : CrackerOptions = CrackerOptions (cliArgs, false)

let cacheKey = mkProjectCacheKey options fsproj |> Async.RunSynchronously

match cacheKey with
| Error _ -> ()
| Ok cacheKey -> cacheKey.Write [| "foo" ; "bar" |]

(*

- if any of the input file change, response file can be different
- impacted cracker options: Configuration, defines
- fable.compiler version

*)

// TODO: referenced projects
