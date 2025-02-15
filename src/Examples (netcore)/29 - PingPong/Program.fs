﻿open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Application
open Aardvark.Application.Slim

[<EntryPoint>]
let main argv = 
    
    Ag.initialize()
    Aardvark.Init()

    printfn "press [Space] to increment texture id."
   

    use app = new OpenGlApplication()
    let win = app.CreateGameWindow(samples = 1)

    let runtime = app.Runtime

    // Given eye, target and sky vector we compute our initial camera pose
    let initialView = CameraView.LookAt(V3d(2.0,2.0,2.0), V3d.Zero, V3d.OOI)
    // the class Frustum describes camera frusta, which can be used to compute a projection matrix.
    let frustum = 
        // the frustum needs to depend on the window size (in oder to get proper aspect ratio)
        win.Sizes 
            // construct a standard perspective frustum (60 degrees horizontal field of view,
            // near plane 0.1, far plane 50.0 and aspect ratio x/y.
            |> Mod.map (fun s -> Frustum.perspective 60.0 0.1 50.0 (float s.X / float s.Y))

    // create a controlled camera using the window mouse and keyboard input devices
    // the window also provides a so called time mod, which serves as tick signal to create
    // animations - seealso: https://github.com/aardvark-platform/aardvark.docs/wiki/animation
    let cameraView = DefaultCameraController.control win.Mouse win.Keyboard win.Time initialView

    // define a dynamic transformation depending on the window's time
    // This time is a special value that can be used for animations which
    // will be evaluated when rendering the scene
    let dynamicTrafo =
        let startTime = System.DateTime.Now
        win.Time |> Mod.map (fun t ->
            let t = (t - startTime).TotalSeconds
            Trafo3d.RotationZ (0.5 * t)
        )

    let box = Box3d(-V3d.III, V3d.III)
    let size = V2i(1024,1024) 

    // Signatures are required to compile render tasks. Signatures can be seen as the `type` of a framebuffer
    // It describes the instances which can be used to exectute the render task (in other words
    // the signature describes the formats and of all render targets which are subsequently used for rendering)
    let signature =
        runtime.CreateFramebufferSignature [
            DefaultSemantic.Colors, { format = RenderbufferFormat.Rgba32f; samples = 1 }
            DefaultSemantic.Depth, { format = RenderbufferFormat.Depth24Stencil8; samples = 1 }
        ]

    let color = [| runtime.CreateTexture(size, TextureFormat.Rgba32f, 1, 1, 1); runtime.CreateTexture(size, TextureFormat.Rgba32f, 1, 1, 1) |]
    let depth = runtime.CreateRenderbuffer(size, RenderbufferFormat.Depth24Stencil8, 1)

    // Create a framebuffer matching signature and capturing the render to texture targets
    let fbos = 
        color |> Array.mapi (fun i e -> 
            runtime.CreateFramebuffer(
                signature, 
                Map.ofList [
                    DefaultSemantic.Colors, ({ texture = e; slice = 0; level = 0 } :> IFramebufferOutput)
                    DefaultSemantic.Depth, (depth :> IFramebufferOutput)
                ]
            )
        )

    // mapped to texture/fbo using `mod` 2
    let currentTexture = Mod.init 0

    // history 
    let texture = currentTexture |> Mod.map (fun i -> color.[i % 2] :> ITexture)
    // current 
    let currentState = currentTexture |> Mod.map (fun i -> color.[(i+1) % 2] :> ITexture)

    // you could use classical ping pong variables as well (as mod) and just change them as needed.

    let task = 
        Sg.box (Mod.constant C4b.Red) (Mod.constant box)
            |> Sg.shader {
                do! DefaultSurfaces.trafo
                do! DefaultSurfaces.vertexColor
                do! DefaultSurfaces.diffuseTexture
            }
            |> Sg.diffuseTexture texture
            // apply the dynamic transformation to the box
            |> Sg.trafo dynamicTrafo
            // extract our viewTrafo from the dynamic cameraView and attach it to the scene graphs viewTrafo 
            |> Sg.viewTrafo (cameraView  |> Mod.map CameraView.viewTrafo )
            // compute a projection trafo, given the frustum contained in frustum
            |> Sg.projTrafo (frustum |> Mod.map Frustum.projTrafo    )
            //|> Sg.depthTest (Mod.init DepthTestMode.None)
            |> Sg.compile runtime signature

    // clear current target
    let rand = RandomSystem()
    let clear = runtime.CompileClear(signature, currentTexture |> Mod.map (fun _ -> rand.UniformC3f().ToC4f()), Mod.constant 1.0)

    // this a custom render task which call others and can be chained with outer render tasks
    // we need this to control where to render to
    let renderToTarget =
        RenderTask.custom (fun (self,token,outputDesc) -> 
            // choose where to render to
            let target = fbos.[(currentTexture.Value+1)%2]
            // manually clear and render to target
            let output = OutputDescription.ofFramebuffer target
            clear.Run(self, token, output)
            task.Run(self, token,output)
        )

    // just visualize using fullscreen quad
    let visualizeCurrentState =
        Sg.fullScreenQuad 
        |> Sg.shader {
             do! DefaultSurfaces.diffuseTexture
           }
        |> Sg.diffuseTexture currentState
        |> Sg.compile runtime win.FramebufferSignature

    win.Keyboard.DownWithRepeats.Values.Add(fun k -> 
        match k with
        | Keys.Space -> 
            transact (fun _ -> currentTexture.Value <- (currentTexture.Value + 1) % 2 )
            printfn "current id: %d" currentTexture.Value
        | _ ->
            ()
    )

    win.RenderTask <- 
        RenderTask.ofList [
            renderToTarget; 
            visualizeCurrentState
        ]


    win.Run()
    0