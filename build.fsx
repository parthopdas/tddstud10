// include Fake libs
#r "./packages/FAKE/tools/FakeLib.dll"

open Fake
open Fake.Testing

// Directories
let buildDir  = "./build/"
let testDir  = "./build/"

// Filesets
let solutionFile = "TddStud10.sln"

// version info
let version = "0.1"  // or retrieve from CI server

// Targets
Target "Clean" (fun _ ->
    CleanDirs [buildDir]
)


Target "Rebuild" DoNothing
Target "Build" (fun _ ->
    !! solutionFile
    |> MSBuild buildDir "Build"
         [
            "Configuration", "Release"
            "Platform", "Any CPU"
            "CreateVsixContainer", "true"
            "DeployExtension", "false"
            "CopyVsixExtensionFiles", "false"
         ]
    |> Log "Build-Output: "

    // AppVeyor workaround
    !! "packages\**\Newtonsoft.Json.dll"
    |> CopyFiles buildDir
)


let runTest pattern =
    fun _ ->
        !! (buildDir + pattern)
        |> xUnit (fun p ->
            { p with
                ToolPath = findToolInSubPath "xunit.console.exe" (currentDirectory @@ "tools" @@ "xUnit")
                WorkingDir = Some testDir })

Target "Test" DoNothing
Target "UnitTests" (runTest "/*.UnitTests*.dll")
Target "ContractTests" (runTest "/*.ContractTests*.dll")


"Clean" ?=> "Build"
"Clean" ==> "Rebuild" 
"Build" ==> "Rebuild" 
"Build" ?=> "UnitTests" ==> "Test"
"Build" ?=> "ContractTests" ==> "Test"
"Rebuild" ==> "Test"

// start build
RunTargetOrDefault "Test"
