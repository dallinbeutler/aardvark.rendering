﻿namespace Aardvark.SceneGraph.Semantics

open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Base.Ag
open Aardvark.SceneGraph
open Aardvark.Base.Rendering



[<Semantic>]
type AsyncLoadSemantics() =

    member x.RenderObjects(app : Sg.AsyncLoadApplicator) : aset<IRenderObject> =
        aset {
            let! child = app.Child
            let runtime = app.Runtime
            for ro in child.RenderObjects() do
                let! prep = 
                    Mod.async (
                        async { 
                            //printfn "preparing render object..."
                            let ro = runtime.PrepareRenderObject(app.FramebufferSignature, ro)
                            //printfn "prepared render object"
                            return ro
                        } 
                    )
                match prep with
                    | None -> ()
                    | Some prep -> yield prep
        }
        |> ASet.mapUse id
        |> ASet.map (fun prep -> prep :> IRenderObject)