﻿namespace Aardvark.SceneGraph

open System
open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Base.Ag
open Aardvark.SceneGraph

open Aardvark.SceneGraph.Internal
open System.Collections.Generic

type RenderGeometryConfig =
    {
        mode                : IndexedGeometryMode
        vertexInputTypes    : Map<Symbol, Type>
        perGeometryUniforms : Map<string, Type>
    }


type RenderCommand =
    internal 
        | REmpty
        | RUnorderedScenes of aset<ISg>
        | RClear of colors : Map<Symbol, IMod<C4f>> * depth : Option<IMod<float>> * stencil : Option<IMod<uint32>>
        | RGeometries of config : RenderGeometryConfig * geometries : aset<IndexedGeometry>
        | ROrdered of alist<RenderCommand>
        | ROrderedConstant of list<RenderCommand>
        | RIfThenElse of condition : IMod<bool> * ifTrue : RenderCommand * ifFalse : RenderCommand
        | RLodTree of config : RenderGeometryConfig * geometries : LodTreeLoader<Geometry>

    static member Empty = REmpty

    static member Clear(colors : Map<Symbol, IMod<C4f>>, depth : Option<IMod<float>>, stencil : Option<IMod<uint32>>) = RClear(colors, depth, stencil)
    static member Clear(colors : Map<Symbol, IMod<C4f>>, depth : IMod<float>, stencil : IMod<uint32>) = RClear(colors, Some depth, Some stencil)
    static member Clear(colors : Map<Symbol, IMod<C4f>>, depth : IMod<float>) = RClear(colors, Some depth, None)
    static member Clear(colors : Map<Symbol, IMod<C4f>>) = RClear(colors, None, None)
    static member Clear(depth : IMod<float>, stencil : IMod<uint32>) = RClear(Map.empty, Some depth, Some stencil)
    static member Clear(depth : IMod<float>) = RClear(Map.empty, Some depth, None)
    static member Clear(stencil : IMod<uint32>) = RClear(Map.empty, None, Some stencil)
    static member Clear(color : IMod<C4f>, depth : Option<IMod<float>>, stencil : Option<IMod<uint32>>) = RClear(Map.ofList [DefaultSemantic.Colors, color], depth, stencil)
    static member Clear(color : IMod<C4f>, depth : IMod<float>, stencil : IMod<uint32>) = RClear(Map.ofList [DefaultSemantic.Colors, color], Some depth, Some stencil)
    static member Clear(color : IMod<C4f>, depth : IMod<float>) = RClear(Map.ofList [DefaultSemantic.Colors, color], Some depth, None)
    static member Clear(color : IMod<C4f>) = RClear(Map.ofList [DefaultSemantic.Colors, color], None, None)

    static member Clear(colors : Map<Symbol, C4f>, depth : Option<float>, stencil : Option<uint32>) = RClear(Map.map (fun _ -> Mod.constant) colors, Option.map Mod.constant depth, Option.map Mod.constant stencil)
    static member Clear(colors : Map<Symbol, C4f>, depth : float, stencil : uint32) = RClear(Map.map (fun _ -> Mod.constant) colors, Some (Mod.constant depth), Some (Mod.constant stencil))
    static member Clear(colors : Map<Symbol, C4f>, depth : float) = RClear(Map.map (fun _ -> Mod.constant) colors, Some (Mod.constant depth), None)
    static member Clear(colors : Map<Symbol, C4f>) = RClear(Map.map (fun _ -> Mod.constant) colors, None, None)
    static member Clear(depth : float, stencil : uint32) = RClear(Map.empty, Some (Mod.constant depth), Some (Mod.constant stencil))
    static member Clear(depth : float) = RClear(Map.empty, Some (Mod.constant depth), None)
    static member Clear(stencil : uint32) = RClear(Map.empty, None, Some (Mod.constant stencil))
    static member Clear(color : C4f, depth : Option<float>, stencil : Option<uint32>) = RClear(Map.ofList [DefaultSemantic.Colors, Mod.constant color], Option.map Mod.constant depth, Option.map Mod.constant stencil)
    static member Clear(color : C4f, depth : float, stencil : uint32) = RClear(Map.ofList [DefaultSemantic.Colors, Mod.constant color], Some (Mod.constant depth), Some (Mod.constant stencil))
    static member Clear(color : C4f, depth : float) = RClear(Map.ofList [DefaultSemantic.Colors, Mod.constant color], Some (Mod.constant depth), None)
    static member Clear(color : C4f) = RClear(Map.ofList [DefaultSemantic.Colors, Mod.constant color], None, None)



    static member Unordered(l : seq<ISg>) = RUnorderedScenes(ASet.ofSeq l)
    static member Unordered(l : list<ISg>) = RUnorderedScenes(ASet.ofList l)
    static member Unordered(l : aset<ISg>) = RUnorderedScenes(l)
    static member Render (s : ISg) = RUnorderedScenes(ASet.single s)
    
    static member Ordered(l : seq<ISg>) = ROrderedConstant(l |> Seq.map RenderCommand.Render |> Seq.toList)
    static member Ordered(l : list<ISg>) = ROrderedConstant(l |> List.map RenderCommand.Render)
    static member Ordered(l : alist<ISg>) = RenderCommand.Ordered(l |> AList.map RenderCommand.Render)

    static member IfThenElse(condition : IMod<bool>, ifTrue : RenderCommand, ifFalse : RenderCommand) = RIfThenElse(condition, ifTrue, ifFalse)
    static member When(condition : IMod<bool>, ifTrue : RenderCommand) = RIfThenElse(condition, ifTrue, REmpty)
    static member WhenNot(condition : IMod<bool>, ifFalse : RenderCommand) = RIfThenElse(condition, REmpty, ifFalse)

    static member LodTree(config : RenderGeometryConfig, geometries : LodTreeLoader<Geometry>) = RLodTree(config,geometries)

    static member Geometries(config : RenderGeometryConfig,  geometries : aset<IndexedGeometry>) = RGeometries(config, geometries)
    static member Geometries(config : RenderGeometryConfig,  geometries : seq<IndexedGeometry>) = RGeometries(config, ASet.ofSeq geometries)
    static member Geometries(config : RenderGeometryConfig,  geometries : list<IndexedGeometry>) = RGeometries(config, ASet.ofList geometries)


    static member Ordered(cmds : list<RenderCommand>) = 
        match cmds with
            | [] -> REmpty
            | _ -> ROrderedConstant cmds

    static member Ordered(cmds : seq<RenderCommand>) =
        RenderCommand.Ordered(Seq.toList cmds)

    static member Ordered(cmds : alist<RenderCommand>) = 
        if cmds.IsConstant then RenderCommand.Ordered (cmds |> AList.toList)
        else ROrdered cmds


[<AutoOpen>]
module ``Sg RuntimeCommand Extensions`` =
    
    module Sg =
        type RuntimeCommandNode(command : RenderCommand) =
            interface ISg
            member x.Command = command


        let execute (cmd : RenderCommand) =
            RuntimeCommandNode(cmd) :> ISg


namespace Aardvark.SceneGraph.Semantics

open Aardvark.Base
open Aardvark.Base.Geometry
open Aardvark.Base.Incremental
open Aardvark.Base.Ag
open Aardvark.SceneGraph

[<AutoOpen>]
module RuntimeCommandSemantics =

    module private RuntimeCommand =
        let rec ofRenderCommand (parent : ISg) (cmd : RenderCommand) =
            match cmd with
                | RenderCommand.REmpty -> 
                    RuntimeCommand.Empty

                | RenderCommand.RUnorderedScenes scenes ->
                    let objects = scenes |> ASet.collect (fun s -> s.RenderObjects())
                    RuntimeCommand.Render(objects)

                | RenderCommand.RClear(colors, depth, stencil) ->
                    RuntimeCommand.Clear(colors, depth, stencil)

                | RenderCommand.RGeometries(config, geometries) ->
                    let effect =
                        match parent.Surface with
                            | Surface.FShadeSimple e -> e
                            | s -> failwithf "[Sg] cannot create GeometryCommand with shader: %A" s

                    let state =
                        {
                            depthTest           = parent.DepthTestMode
                            depthBias           = parent.DepthBias
                            cullMode            = parent.CullMode
                            frontFace           = parent.FrontFace
                            blendMode           = parent.BlendMode
                            fillMode            = parent.FillMode
                            stencilMode         = parent.StencilMode
                            multisample         = parent.Multisample
                            writeBuffers        = parent.WriteBuffers
                            globalUniforms      = new Providers.UniformProvider(Ag.getContext(), parent.Uniforms, [])
                            geometryMode        = config.mode
                            vertexInputTypes    = config.vertexInputTypes
                            perGeometryUniforms = config.perGeometryUniforms
                        }

                    RuntimeCommand.Geometries(effect, state, geometries)
            
                | RenderCommand.ROrdered(list) ->
                    let commands = list |> AList.map (ofRenderCommand parent)
                    RuntimeCommand.Ordered(commands)

                | RenderCommand.ROrderedConstant(list) ->
                    let commands = list |> List.map (ofRenderCommand parent)
                    RuntimeCommand.Ordered(AList.ofList commands)

                | RenderCommand.RIfThenElse(c,t,f) ->
                    RuntimeCommand.IfThenElse(c, ofRenderCommand parent t, ofRenderCommand parent f)

                | RenderCommand.RLodTree(config,g) ->
                    let state =
                        {
                            depthTest           = parent.DepthTestMode
                            depthBias           = parent.DepthBias
                            cullMode            = parent.CullMode
                            frontFace           = parent.FrontFace
                            blendMode           = parent.BlendMode
                            fillMode            = parent.FillMode
                            stencilMode         = parent.StencilMode
                            multisample         = parent.Multisample
                            writeBuffers        = parent.WriteBuffers
                            globalUniforms      = new Providers.UniformProvider(Ag.getContext(), parent.Uniforms, [])
                            geometryMode        = config.mode
                            vertexInputTypes    = config.vertexInputTypes
                            perGeometryUniforms = config.perGeometryUniforms
                        }

                    RuntimeCommand.LodTree(parent.Surface, state, g)

    [<Semantic>]
    type RuntimeCommandSem() =
        member x.RenderObjects(n : Sg.RuntimeCommandNode) : aset<IRenderObject> =
            let cmd = n.Command
            let runtimeCommand = RuntimeCommand.ofRenderCommand n cmd

            let pass = n.RenderPass
            let scope = Ag.getContext()

            let obj = CommandRenderObject(pass, scope, runtimeCommand)
            ASet.single (obj :> IRenderObject)

