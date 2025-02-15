﻿open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Application
open Aardvark.Application.Utilities
open FShade
open Aardvark.Application.Slim
open System.Diagnostics
open System

module Shader =
    open FShade 
    
    let samplerArray = 
        sampler2d {
            textureArray uniform?TextureArray 28
            filter Filter.MinMagMipLinear
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    let sampleTextureArray (v : Effects.Vertex) =
        fragment {
            
            let cnt : int = uniform?TextureCount
            let crdOff : V2d = uniform?BlaBuffer?CoordOff
            let mutable color = V3d.OOO
            for i in 0..cnt-1 do
                color <- color + samplerArray.[i].Sample(v.tc + crdOff * 3.0).XYZ

            if cnt > 0 then
                color <- color / float cnt

            return V4d(v.c.XYZ + color, 1.0)
        }      

    let samplerArraySeparate = 
        [|
            sampler2d {
                texture uniform?TextureFoo
                filter Filter.MinMagMipLinear
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            };
            sampler2d {
                texture uniform?TextureBar
                filter Filter.MinMagMipLinear
                addressU WrapMode.Wrap
                addressV WrapMode.Wrap
            };
            sampler2d {
                texture uniform?TextureFoobar
                filter Filter.MinMagMipPoint
                addressU WrapMode.Wrap
                addressV WrapMode.Wrap
            };
        |]

    let sampleTextureArray2 (v : Effects.Vertex) =
        fragment {
            
            let mutable color = V3d.OOO
            for i in 0..2 do
                color <- color + samplerArraySeparate.[i].Sample(v.tc).XYZ
                
            color <- color / 3.0

            return V4d(v.c.XYZ * 0.5 + color * 0.5, 1.0)
        }  

    let singleSampler =
        sampler2d {
            texture uniform?SingleTexture
            filter Filter.MinMagMipLinear
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }
    
    let sampleSingleTexture (v : Effects.Vertex) =
        fragment {
            
            let color = singleSampler.Sample(v.tc).XYZ

            return V4d(v.c.XYZ * 0.5 + color * 0.5, 1.0)
        } 
      
    type VertexIn = {
        [<WorldPosition>] wp : V4d
    }

    type VertexOut = {
        [<WorldPosition>] wp : V4d
        [<ClipDistance>] cd : float[]
    }

    let clipVs (v : VertexIn) =
        vertex {
            let planeCoefficients : V4d = uniform?ClipPlaneCoefficients
            let clidDistance = V4d.Dot(v.wp, planeCoefficients)
            return { wp = v.wp
                     cd = [| clidDistance |] }
        }



[<EntryPoint>]
let main argv = 
    
    // first we need to initialize Aardvark's core components
    Ag.initialize()
    Aardvark.Init()

    let app = new OpenGlApplication()
    let win = app.CreateGameWindow(4)


    let rand = RandomSystem(1)
    let rndTex() =
        let img = PixImage<byte>(Col.Format.RGBA, V2i(64, 64))
        img.GetMatrix<C4b>().SetByIndex (fun (i : int64) ->
            rand.UniformC3f().ToC4b()
        ) |> ignore
            
        PixTexture2d(PixImageMipMap([| img :> PixImage |]), TextureParams.mipmapped) :> ITexture

    let textures = Array.init 28 (fun i -> rndTex())

    let textureFoo = rndTex()
    let textureBar = rndTex()
    let textureFoobar = rndTex()

    let tetureSingleMod = Mod.init (rndTex())

    let scene = CSet.empty

    let count = Mod.init 1

    let addStuff (number : int) = 
        
        transact (fun _ -> 
            for i in 0..number-1 do
                //let box = Primitives.unitSphere 7
                let box = IndexedGeometryPrimitives.solidPhiThetaSphere (Sphere3d.Unit) (10) (C4b.Red)
                let pos = rand.UniformV3d() * 100.0 - 50.0
                scene.Add((box, Trafo3d.Translation(pos))) |> ignore
            )
        ()


    addStuff 5000

    let case = Mod.init 0

    win.Keyboard.DownWithRepeats.Values.Add (fun k ->
        match k with
            | Keys.Add -> let cnt = count.GetValue()
                          if cnt < textures.Length - 1 then
                                transact(fun _ -> count.Value <- cnt + 1)
                                printfn "TextureCount=%d" (cnt + 1)
            | Keys.Subtract -> let cnt = count.GetValue()
                               if cnt > 1 then
                                 transact(fun _ -> count.Value <- cnt - 1)
                                 printfn "TextureCount=%d" (cnt - 1)
            | Keys.T -> 

                let cur = case.GetValue();
                transact(fun _ -> case.Value <- (cur + 1) % 5)
                () 
            | Keys.Z ->
                transact(fun _ -> Mod.change tetureSingleMod (rndTex()))
            | _ ->
                ()
    )


    let cases : aset<ISg> = case |> ASet.bind (fun cs ->
                    match cs with
                    | 0 ->  printfn "\nGlobal Uniforms"

                            let drawGeos = Sg.set (scene |> ASet.map (fun (ig, trafo) ->
                                        Sg.ofIndexedGeometry ig
                                            |> Sg.trafo (Mod.constant trafo)
                                            |> Sg.vertexBufferValue DefaultSemantic.DiffuseColorCoordinates (Mod.init V4f.Zero)
                                            |> Sg.uniform "CoordOff" (Mod.init (rand.UniformV2d()))))

                            let mutable sg = drawGeos
                                            |> Sg.uniform "TextureCount" count

                            for i in 0..textures.Length-1 do
                                sg <- sg |> Sg.uniform ("TextureArray" + i.ToString()) (Mod.init textures.[i]) 

                            //sg <- sg |> Sg.uniform "TextureArray" (Mod.constant textures)

                            ASet.single sg

                    | 1 ->  printfn "\nGlobal UniformMap"

                            let drawGeos = Sg.set (scene |> ASet.map (fun (ig, trafo) ->
                                                Sg.ofIndexedGeometry ig
                                                            |> Sg.trafo (Mod.constant trafo)
                                                            |> Sg.vertexBufferValue DefaultSemantic.DiffuseColorCoordinates (Mod.init V4f.Zero)
                                                            |> Sg.uniform "CoordOff" (Mod.init (rand.UniformV2d()))
                                                            |> Sg.uniform "TextureCount" count
                                        ))

                                    
                            let mutable map = Map.empty<Symbol, IMod>
                                        
                            for i in 0..textures.Length-1 do
                                map <- map.Add(Symbol.Create ("TextureArray" + i.ToString()), Mod.init textures.[rand.UniformInt(textures.Length)])

                            let sg = Sg.UniformApplicator(map, drawGeos)

                            ASet.single (sg :> ISg)

                    | 2 ->  printfn "\nPerGeometry Uniforms"

                            let sg = Sg.set (scene |> ASet.map (fun (ig, trafo) ->

                                        let mutable sg = Sg.ofIndexedGeometry ig
                                                            |> Sg.trafo (Mod.constant trafo)
                                                            |> Sg.vertexBufferValue DefaultSemantic.DiffuseColorCoordinates (Mod.init V4f.Zero)
                                                            |> Sg.uniform "CoordOff" (Mod.init (rand.UniformV2d()))
                                                            |> Sg.uniform "TextureCount" count
                                            
                                        for i in 0..textures.Length-1 do
                                            sg <- sg |> Sg.uniform ("TextureArray" + i.ToString()) (Mod.init textures.[rand.UniformInt(textures.Length)]) 

                                        sg 
                                        ))
                            ASet.single sg

                    | 3 ->  printfn "\nPerGeometry UniformMap"

                            let sg = Sg.set (scene |> ASet.map (fun (ig, trafo) ->

                                        let sg = Sg.ofIndexedGeometry ig
                                                            |> Sg.trafo (Mod.constant trafo)
                                                            |> Sg.vertexBufferValue DefaultSemantic.DiffuseColorCoordinates (Mod.init V4f.Zero)
                                                            |> Sg.uniform "CoordOff" (Mod.init (rand.UniformV2d()))
                                                            |> Sg.uniform "TextureCount" count
                                                            
                                        let mutable map = Map.empty<Symbol, IMod>
                                        
                                        for i in 0..textures.Length-1 do
                                            map <- map.Add(Symbol.Create ("TextureArray" + i.ToString()), Mod.init textures.[rand.UniformInt(textures.Length)])

                                        let sg = Sg.UniformApplicator(map, sg)
                                        
                                        sg :> ISg
                                        ))

                            ASet.single sg

                    | _ -> ASet.empty
            )

    let sg = Sg.set (cases)
                |> Sg.shader {
                    do! DefaultSurfaces.trafo
                    do! DefaultSurfaces.constantColor(C4f.Black)
                    do! Shader.clipVs
                    do! Shader.sampleTextureArray
                    do! Shader.sampleTextureArray2
                    do! Shader.sampleSingleTexture
                    }

                |> Sg.simpleOverlay win
                |> Sg.uniform "ClipPlaneCoefficients" (Mod.constant (V4d(0, 1, 0, 0)))
                |> Sg.texture (Symbol.Create "TextureFoo") (Mod.constant textureFoo)
                |> Sg.texture (Symbol.Create "TextureBar") (Mod.constant textureBar)
                |> Sg.texture (Symbol.Create "TextureFoobar") (Mod.constant textureFoobar)
                |> Sg.texture (Symbol.Create "SingleTexture") tetureSingleMod


    let rt = app.Runtime.CompileRender(win.FramebufferSignature, BackendConfiguration.Native, sg)

    let sw = Stopwatch()

    let task =
        { new AbstractRenderTask() with
            member x.FramebufferSignature = rt.FramebufferSignature
            member x.Runtime = rt.Runtime
            member x.PerformUpdate (a,b) = rt.Update(a,b)
            member x.Perform (t,a,b) = 
                sw.Restart()
                let stats = RenderToken.Zero
                use __ = app.Runtime.Context.ResourceLock
                OpenTK.Graphics.OpenGL4.GL.Enable(OpenTK.Graphics.OpenGL4.EnableCap.ClipDistance0)
                rt.Run(t,stats,b)
                sw.Stop()
                if sw.Elapsed.TotalSeconds > 0.1 then 
                    Log.warn "long frame: %A" sw.MicroTime
                    Log.line "DrawCount: %d" stats.DrawCallCount
                    Log.line "Instructions: %d" stats.TotalInstructions
                                

            member x.Release() = rt.Dispose()
            member x.Use f = rt.Use f
        }

    win.RenderTask <- task
    win.Run()

    transact (fun () -> 
        task.Dispose())

    0
