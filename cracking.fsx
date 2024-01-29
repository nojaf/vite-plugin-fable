#I "./artifacts/bin/Fable.Daemon/debug"
#r "Fable.Compiler.dll"
#r "Fable.Daemon.dll"

open Fable.Compiler.ProjectCracker
open Fable.Daemon.CoolCatCracking

fsi.AddPrinter (fun (x : ProjectOptionsResponse) ->
    $"ProjectOptionsResponse: %i{x.ProjectOptions.Length} options, %i{x.ProjectReferences.Length} references, %s{x.TargetFramework.Value}, %s{x.OutputType.Value}"
)

let fsproj = "/home/nojaf/projects/vite-plugin-fable/sample-project/App.fsproj"

let result =
    coolCatResolver.GetProjectOptionsFromProjectFile (true, Unchecked.defaultof<CrackerOptions>, fsproj)

for option in result.ProjectOptions do
    printfn "%s" option
