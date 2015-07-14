﻿namespace Aardvark.Rendering.GL

open System
open System.Collections.Generic
open Aardvark.Base
open Aardvark.Rendering
open OpenTK.Graphics.OpenGL4
open Aardvark.Base.Incremental

[<AutoOpen>]
module RenderTasks =
    open System
    open System.Runtime.InteropServices
    open System.Collections.Generic
    open Aardvark.Rendering.GL
    open Aardvark.Rendering.GL.OpenGl.Enums




    let private configs = Dictionary<uint64, Order>()

    let setPassConfig (pass : uint64, config : Order) =
        configs.[pass] <- config

    let getPassConfig(pass : uint64) =
        match configs.TryGetValue pass with
            | (true, c) -> c
            | _ -> Order.Unordered


    type SortedList<'k, 'v when 'k : comparison>() =
        let keys = List<'k>()
        let values = List<'v>()

        let rec findIndexForKey' (key : 'k) (l : int) (r : int) =
            if l <= r then
                let m = (l+r) / 2
                let v = keys.[m]
                let c = compare key v
                if c = 0 then
                    Right m
                elif c > 0 then
                    findIndexForKey' key (m+1) r
                else
                    findIndexForKey' key l (m-1)
            else
                Left l

        let findIndexForKey (key : 'k) =
            findIndexForKey' key 0 (keys.Count - 1)

        //Right = found
        //Left = not found

        member x.Add(key : 'k, value : 'v) =
            match findIndexForKey key with
                | Left index ->
                    keys.Insert(index, key)
                    values.Insert(index, value)
                | _ -> failwith "duplicated entry"
               
        member x.Remove(key : 'k) =
            match findIndexForKey key with
                | Right index -> 
                    keys.RemoveAt index
                    values.RemoveAt index 
                    true
                | _ -> false     

        member x.TryGetValue(key : 'k, [<Out>] value : byref<'v>) =
            match findIndexForKey key with
                | Right index -> 
                    value <- values.[index]  
                    true
                | _ -> false       
                
        member x.TryGetNextGreater(key : 'k, [<Out>] value : byref<'v>) =
            let nextIndex = match findIndexForKey key with
                            | Left index -> if index < 0 then 0 else index + 1
                            | Right index -> index + 1
            if nextIndex < values.Count then
                value <- values.[nextIndex]
                true
            else
                false

        member x.TryGetNextSmaller(key : 'k, [<Out>] value : byref<'v>) =
            let prevIndex = match findIndexForKey key with
                            | Left index -> if index >= values.Count then values.Count - 1 else index - 1
                            | Right index -> index - 1
            if prevIndex >= 0 then
                value <- values.[prevIndex]
                true
            else
                false

        member x.Count = keys.Count

        member x.Clear() =
            keys.Clear()
            values.Clear()

        member x.Item
            with get i = values.[i]
            and set i v = values.[i] <- v

        interface IEnumerable<KeyValuePair<'k, 'v>> with
            member x.GetEnumerator() : IEnumerator<KeyValuePair<'k, 'v>> =
                let s = seq { for i in 0..keys.Count-1 do yield KeyValuePair(keys.[i], values.[i]) }
                s.GetEnumerator()

            member x.GetEnumerator() : System.Collections.IEnumerator =
                let s = seq { for i in 0..keys.Count-1 do yield KeyValuePair(keys.[i], values.[i]) }
                (s :> System.Collections.IEnumerable).GetEnumerator()

    type RenderTask(runtime : IRuntime, ctx : Context, manager : ResourceManager, engine : ExecutionEngine, set : aset<RenderJob>) as this =
        inherit AdaptiveObject()

        let subscriptions = Dictionary()
        let reader = set.GetReader()
        do reader.AddOutput this

        let inputs = ReferenceCountingSet<IAdaptiveObject>()
        let mutable programs = Map.empty
        let changer = Mod.init ()
        let surfaceSubscriptions = Dictionary<RenderJob, IDisposable>()

        let mutable additions = 0
        let mutable removals = 0

        let addInput m =
            if inputs.Add m then
                transact (fun () -> m.AddOutput this)

        let removeInput m =
            if inputs.Remove m then
                transact (fun () -> m.RemoveOutput this)

        let tryGetProgramForPass (pass : uint64) =
            Map.tryFind pass programs

        let getProgramForPass (pass : uint64) (scope : Ag.Scope) =
            match Map.tryFind pass programs with
                | Some p -> p
                | _ -> 
                    let config = getPassConfig pass

                    let p = 
                        match config with
                            | Order.Unordered -> 
                                new Compiler.RedundancyRemovalProgram<_>(Compiler.FragmentHandlers.glvm, manager, addInput, removeInput) :> IProgram

//                                let mode = 
//                                    if engine &&& ExecutionEngine.Managed <> ExecutionEngine.None then 0
//                                    elif engine &&& ExecutionEngine.Unmanaged <> ExecutionEngine.None then 1
//                                    else 2
//
//                                let opt =
//                                    if engine &&& ExecutionEngine.Optimized <> ExecutionEngine.None then 2
//                                    elif engine &&& ExecutionEngine.RuntimeOptimized <> ExecutionEngine.None then 1
//                                    else 1
//
//                                match mode with
//                                    | 2 -> new OptimizedNativeProgram(manager, addInput, removeInput) :> IProgram
//                                    | 1 ->
//                                        match opt with
//                                            | 0 -> new UnoptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
//                                            | 1 -> new RuntimeOptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
//                                            | _ -> new OptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
//                                    | _ ->
//                                        new OptimizedManagedProgram(manager,addInput,removeInput) :> IProgram

                                //new UnoptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
                                //new RuntimeOptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
                                //new OptimizedSwitchProgram(manager, addInput, removeInput) :> IProgram
                                //new OptimizedNativeProgram(manager, addInput, removeInput) :> IProgram
                                

                                //new OptimizedNativeProgram(manager, addInput, removeInput) :> IProgram
                                //new OptimizedManagedProgram(manager,addInput,removeInput) :> IProgram
                                //new OptimizedSwitchProgram(manager,addInput,removeInput) :> IProgram
                            | order ->
                                //let sorter = fun () -> BspSorting.BBTreeSorter(scope, bspOrder) :> ISorter
                                new SortedProgram(order, manager, addInput, removeInput, Sorting.createSorter scope order) :> IProgram

                    programs <- Map.add pass p programs
                    p


        member private x.Add(pass : uint64, rj : RenderJob) =
            additions <- additions + 1
            let program = getProgramForPass pass (rj.AttributeScope |> unbox)
            program.Add rj

            let subscription =
                let initial = ref true
                rj.Surface |> Mod.registerCallback (fun s ->
                    if !initial then initial := false
                    else 
                        program.Update rj
                        x.MarkOutdated()
                )
            surfaceSubscriptions.Add(rj, subscription)

        member private x.Remove(pass : uint64, rj : RenderJob) =
            removals <- removals + 1
            match tryGetProgramForPass pass with
                | Some p -> p.Remove rj
                | None -> ()

            match surfaceSubscriptions.TryGetValue rj with
                | (true, s) -> 
                    s.Dispose()
                    Dictionary.remove rj surfaceSubscriptions |> ignore
                | _ -> ()

        member x.ProcessDeltas (deltas : list<Delta<RenderJob>>) =
            for d in deltas do
                match d with
                    | Add a ->
                        if a.RenderPass <> null then
                            let oldPass = ref System.UInt64.MaxValue
                            let s = a.RenderPass |> Mod.registerCallback (fun k ->
                                if !oldPass <> k  // phantom change here might lead to duplicate additions.
                                    then
                                        oldPass := k
                                        x.Add(k,a)

                                        match subscriptions.TryGetValue a with
                                            | (true,(s,old)) ->
                                                x.Remove(old, a)
                                                subscriptions.[a] <- (s,k)
                                            | _ -> ()
                                    else 
                                        printfn "changed pass to old value (phantom)"
                            
                            )
                            let sortKey = a.RenderPass.GetValue()
                            subscriptions.[a] <- (s, sortKey)
                        else
                            x.Add(0UL, a)

                    | Rem a ->
                        if a.RenderPass <> null then
                            match subscriptions.TryGetValue a with
                                | (true,(d,k)) ->
                                    x.Remove(k, a)
                                    d.Dispose()
                                    subscriptions.Remove a |> ignore
                                | _ -> ()
                        else
                            x.Remove(0UL, a)

        member x.Run (fbo : IFramebuffer) =
            x.EvaluateAlways (fun () ->
                using ctx.ResourceLock (fun _ ->
                    let old = Array.create 4 0
                    let mutable oldFbo = 0
                    OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.Viewport, old)
                    OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo)

                    let fboHandle = fbo |> unbox<Framebuffer>
                    let handle = fboHandle.Handle

                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, handle)
                    GL.Check "could not bind framebuffer"
                    GL.Viewport(0, 0, fbo.Size.X, fbo.Size.Y)
                    GL.Check "could not set viewport"

                    if reader.OutOfDate then
                        x.ProcessDeltas (reader.GetDelta())

                    let mutable stats = FrameStatistics.Zero
                    let contextHandle = ContextHandle.Current.Value

                    //render
                    for (KeyValue(_,p)) in programs do
                        stats <- stats + p.Run(fboHandle, contextHandle)


                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, oldFbo)
                    GL.Check "could not bind framebuffer"
                    GL.Viewport(old.[0], old.[1], old.[2], old.[3])
                    GL.Check "could not set viewport"

                    RenderingResult(fbo, stats)
                )
            )

        member x.Dispose() = 
            for _,p in Map.toSeq programs do
                p.Dispose()
            programs <- Map.empty
            reader.RemoveOutput x
            reader.Dispose()


        interface IRenderTask with
            member x.Run(fbo) =
                x.Run(fbo)

            member x.Dispose() =
                x.Dispose()


    type ClearTask(runtime : IRuntime, color : IMod<C4f>, depth : IMod<float>, ctx : Context) as this =
        inherit AdaptiveObject()
        do color.AddOutput this
           depth.AddOutput this

        member x.Run(fbo : IFramebuffer) =
            using ctx.ResourceLock (fun _ ->
                x.EvaluateAlways (fun () ->
                    let old = Array.create 4 0
                    let mutable oldFbo = 0
                    OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.Viewport, old)
                    OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo)

                    let handle = (fbo.Handle |> unbox<int>)

                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, handle)
                    GL.Viewport(0, 0, fbo.Size.X, fbo.Size.Y)
                    GL.Check "could not bind framebuffer"

                    let c = Mod.force color
                    let d = Mod.force depth
                    GL.ClearColor(c.R, c.G, c.B, c.A)
                    GL.ClearDepth(d)
                    GL.Clear(ClearBufferMask.ColorBufferBit ||| ClearBufferMask.DepthBufferBit)

                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, oldFbo)
                    GL.Viewport(old.[0], old.[1], old.[2], old.[3])
                    GL.Check "could not bind framebuffer"

                    RenderingResult(fbo, FrameStatistics.Zero)
                )
            )

        member x.Dispose() =
            color.RemoveOutput x
            depth.RemoveOutput x

        interface IRenderTask with
            member x.Run(fbo) =
                x.Run(fbo)

            member x.Dispose() =
                x.Dispose()
