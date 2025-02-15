﻿namespace Aardvark.SceneGraph

open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Base.Rendering


[<AutoOpen>]
module LodTreeRendering =

    type LodTreeRenderConfig =
        {
            budget : IMod<int64>
            splitfactor : IMod<float>
            time : IMod<System.DateTime>
            maxSplits : IMod<int>
            renderBounds : IMod<bool>
            stats : IModRef<LodRendererStats>
            pickTrees : Option<mmap<ILodTreeNode,SimplePickTree>>
            alphaToCoverage : bool
        }

    module LodTreeRenderConfig =
        let simple =
            {
                budget = Mod.constant -1L
                splitfactor = Mod.constant 0.4
                time = Mod.time
                maxSplits = Mod.constant System.Environment.ProcessorCount
                renderBounds = Mod.constant false
                stats = Mod.init Unchecked.defaultof<_>
                pickTrees = None
                alphaToCoverage = false
            }

    module Sg = 
        type LodTreeNode(stats : IModRef<LodRendererStats>, pickTrees : Option<mmap<ILodTreeNode,SimplePickTree>>, alphaToCoverage : bool, budget : IMod<int64>, splitfactor : IMod<float>, renderBounds : IMod<bool>, maxSplits : IMod<int>, time : IMod<System.DateTime>, clouds : aset<LodTreeInstance>) =
            member x.Time = time
            member x.Clouds = clouds
            member x.MaxSplits = maxSplits

            member x.Stats = stats
            member x.PickTrees = pickTrees
            member x.RenderBounds = renderBounds
            member x.Budget = budget
            member x.AlphaToCoverage = alphaToCoverage
            member x.SplitFactor = splitfactor
            interface ISg

            new(stats : IModRef<LodRendererStats>, pickTrees : mmap<ILodTreeNode,SimplePickTree>, alphaToCoverage : bool, budget : IMod<int64>, splitfactor : IMod<float>, renderBounds : IMod<bool>, maxSplits : IMod<int>, time : IMod<System.DateTime>, clouds : aset<LodTreeInstance>) =
                LodTreeNode(stats, Some pickTrees, alphaToCoverage, budget, splitfactor, renderBounds, maxSplits, time, clouds)
            new(stats : IModRef<LodRendererStats>, alphaToCoverage : bool, budget : IMod<int64>, splitfactor : IMod<float>, renderBounds : IMod<bool>, maxSplits : IMod<int>, time : IMod<System.DateTime>, clouds : aset<LodTreeInstance>) =
                LodTreeNode(stats, None, alphaToCoverage, budget, splitfactor, renderBounds, maxSplits, time, clouds)

        let lodTree (cfg : LodTreeRenderConfig) (data : aset<LodTreeInstance>) =
            LodTreeNode(cfg.stats, cfg.alphaToCoverage, cfg.budget, cfg.splitfactor, cfg.renderBounds, cfg.maxSplits, cfg.time, data) :> ISg
    

namespace Aardvark.SceneGraph.Semantics

open Aardvark.Base
open Aardvark.Base.Ag
open Aardvark.Base.Incremental
open Aardvark.SceneGraph  

[<Semantic>]
type LodNodeSem() =
    member x.RenderObjects(sg : Sg.LodTreeNode) =
        let scope = Ag.getContext()
        let state = PipelineState.ofScope scope
        let surface = sg.Surface
        let pass = sg.RenderPass

        let model = sg.ModelTrafo
        let view = sg.ViewTrafo
        let proj = sg.ProjTrafo

        let id = newId()
        let obj =
            { new ICustomRenderObject with
                member x.Id = id
                member x.AttributeScope = scope
                member x.RenderPass = pass
                member x.Create(r, fbo) = 
                    let config =
                        {
                            fbo = fbo
                            time = sg.Time
                            surface = surface
                            state = state
                            pass = pass
                            model = model
                            view = view
                            proj = proj
                            budget = sg.Budget
                            renderBounds = sg.RenderBounds
                            maxSplits = sg.MaxSplits
                            splitfactor = sg.SplitFactor
                            stats = sg.Stats
                            pickTrees = sg.PickTrees
                            alphaToCoverage = sg.AlphaToCoverage
                        }

                    r.CreateLodRenderer(config, sg.Clouds)
            }

        ASet.single (obj :> IRenderObject)
