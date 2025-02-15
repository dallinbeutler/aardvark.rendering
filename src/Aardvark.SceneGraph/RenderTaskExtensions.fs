﻿namespace Aardvark.SceneGraph

open System
open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Base.Rendering

[<AutoOpen>]
module ExtendedDefaultSemantics =
    
    module DefaultSemantic =
        let ColorTexture = Symbol.Create "ColorTexture"
        let DepthTexture = Symbol.Create "DepthTexture"


module RenderTask =
    open FShade

    module private Shaders =
        open FShade

        type Vertex = { [<Position>] pos : V4d; [<TexCoord>] tc : V2d }
        type Fragment = { [<Color>] color : V4d; [<Depth>] depth : float }

        let colorSampler =
            sampler2d {
                texture uniform?ColorTexture
                filter Filter.MinMagPoint
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            }

        let depthSampler =
            sampler2d {
                texture uniform?DepthTexture
                filter Filter.MinMagPoint
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            }


        let fs (v : Vertex) =
            fragment {
                let c = colorSampler.Sample(v.tc)
                let d = depthSampler.Sample(v.tc).X

                return { color = c; depth = d }
            }

    module RenderObjects =
        
        let private emptyUniforms =
            { new IUniformProvider with
                member x.TryGetUniform(scope, name) = None
                member x.Dispose() = ()
            }

        let private uniformProvider (color : IMod<ITexture>) (depth : IMod<ITexture>) =
            { new IUniformProvider with
                member x.TryGetUniform(scope : Ag.Scope, semantic : Symbol) =
                    if semantic = DefaultSemantic.ColorTexture then Some (color :> IMod)
                    elif semantic = DefaultSemantic.DepthTexture then Some (depth :> IMod)
                    else None

                member x.Dispose() =
                    ()
            }


        let private emptyAttributes =
            { new IAttributeProvider with
                member x.All = Seq.empty
                member x.TryGetAttribute name = None
                member x.Dispose() = ()
            }

        let private attributeProvider =
            let positions =  ArrayBuffer ([|V3f(-1.0f, -1.0f, 1.0f); V3f(1.0f, -1.0f, 1.0f); V3f(1.0f, 1.0f, 1.0f); V3f(-1.0f, 1.0f, 1.0f)|] :> Array)
            let texCoords = ArrayBuffer ([|V2f.OO; V2f.IO; V2f.II; V2f.OI|] :> Array)

            let pView = BufferView(Mod.constant (positions :> IBuffer), typeof<V3f>)
            let tcView = BufferView(Mod.constant (texCoords :> IBuffer), typeof<V2f>)

            { new IAttributeProvider with
                member x.All = 
                    Seq.ofList [
                        DefaultSemantic.Positions, pView
                        DefaultSemantic.DiffuseColorCoordinates, tcView
                    ]
                member x.TryGetAttribute(name : Symbol) = 
                    if name = DefaultSemantic.Positions then Some pView
                    elif name = DefaultSemantic.DiffuseColorCoordinates then Some tcView
                    else None
                member x.Dispose() = ()
            }

        let baseObject =
            { RenderObject.Create() with
                AttributeScope = Ag.emptyScope
                IsActive = Mod.constant true
                RenderPass = RenderPass.main
                DrawCallInfos = Mod.constant [DrawCallInfo(InstanceCount = 1, FaceVertexCount = 6)]
                Mode = IndexedGeometryMode.TriangleList
                Surface = Shaders.fs |> toEffect |> Surface.FShadeSimple
                DepthTest = Mod.constant DepthTestMode.LessOrEqual
                CullMode = Mod.constant CullMode.None
                BlendMode = Mod.constant BlendMode.Blend
                FillMode = Mod.constant FillMode.Fill
                StencilMode = Mod.constant StencilMode.Disabled
                Indices = BufferView(Mod.constant (ArrayBuffer [|0;1;2; 0;2;3|] :> IBuffer), typeof<int>) |> Some
                InstanceAttributes = emptyAttributes
                VertexAttributes = attributeProvider
                Uniforms = emptyUniforms
            }

        let create (color : IMod<ITexture>) (depth : IMod<ITexture>) =
            { RenderObject.Clone(baseObject) with
                Uniforms = uniformProvider color depth
            }

    let cache (t : IRenderTask) : IRenderTask =
        
        match t.Runtime, t.FramebufferSignature with
            | Some runtime, Some signature ->
                
                let size = Mod.init V2i.II

                let color, depth =
                    t |> RenderTask.renderToColorAndDepth size

                let compose =
                    ASet.ofList [
                        RenderObjects.create color depth :> IRenderObject
                    ]

                let composeTask = runtime.CompileRender(signature, compose)


                RenderTask.ofList [
                    RenderTask.custom (fun (self, token, target) ->
                        if target.framebuffer.Size <> size.Value then
                            transact (fun () -> Mod.change size target.framebuffer.Size)

                    )
                    composeTask
                ]

            | _, _ ->
                Log.warn "[RenderTask] unable to cache RenderTask since it has no Runtime"
                t

    let postProcess (effect : list<FShadeEffect>) (t : IRenderTask) =
        match t.Runtime, t.FramebufferSignature with
            | Some runtime, Some signature ->
                
                let size = Mod.init V2i.II

                let color, depth =
                    t |> RenderTask.renderToColorAndDepth size

                let compose =
                    ASet.ofList [
                        { RenderObjects.create color depth with
                            Surface = effect |> FShade.Effect.compose |> Surface.FShadeSimple
                        }:> IRenderObject
                    ]

                let composeTask = runtime.CompileRender(signature, compose)


                RenderTask.ofList [
                    RenderTask.custom (fun (self, token, target) ->
                        if target.framebuffer.Size <> size.Value then
                            transact (fun () -> Mod.change size target.framebuffer.Size)

                    )
                    composeTask
                ]

            | _ ->
                Log.warn "[RenderTask] unable to postProcess RenderTask since it has no Runtime"
                t
