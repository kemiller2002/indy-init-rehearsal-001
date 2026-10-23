/// The vendored frozen Causa release (data/causa-storage-v1.0.0), exactly as scripts/build-site.mjs bundles it.
module App.Engine.Tests.TestData

open System
open System.IO
open System.Text.Json

let releaseRoot =
    let rec up (dir: DirectoryInfo) =
        let candidate = Path.Combine(dir.FullName, "data", "causa-storage-v1.0.0")
        if Directory.Exists candidate then candidate
        else match dir.Parent with null -> failwith "data/causa-storage-v1.0.0 not found" | parent -> up parent
    up (DirectoryInfo AppContext.BaseDirectory)

/// Release path -> exact bytes, for the same selection the site bundle carries.
let releaseFiles : Map<string, byte[]> =
    let rel (p: string) = Path.GetRelativePath(releaseRoot, p).Replace('\\', '/')
    let pick (dir: string) =
        Directory.GetFiles(Path.Combine(releaseRoot, dir), "*", SearchOption.AllDirectories) |> Array.map (fun p -> rel p, File.ReadAllBytes p)
    Array.concat
        [ pick "contract/v1.0.0/schemas"
          pick "contract/v1.0.0/golden"
          pick "fixtures/v1.0.0/investigations"
          [| "fixtures/v1.0.0/manifest.json", File.ReadAllBytes(Path.Combine(releaseRoot, "fixtures/v1.0.0/manifest.json")) |] ]
    |> Map.ofArray

/// The bundle document the app fetches (./data/causa-storage-v1.0.0.bundle.json).
let bundleOf (files: Map<string, byte[]>) =
    JsonSerializer.Serialize(
        dict [ "tag", box "causa-storage-v1.0.0"; "files", box (files |> Map.map (fun _ b -> Convert.ToBase64String b) |> Map.toSeq |> dict) ]
    )

let bundleJson = lazy (bundleOf releaseFiles)

/// The verified dataset (schemas and the five frozen investigations), loaded once.
let dataset =
    lazy
        (match IndyInitRootCauseInvestigator.Engine.CausaData.loadDataset releaseFiles with
         | Ok d -> d
         | Error e -> failwith (IndyInitRootCauseInvestigator.Engine.CausaData.describe e))
