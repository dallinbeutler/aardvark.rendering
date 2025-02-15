﻿namespace Aardvark.Rendering.GL

#nowarn "9"

open System
open System.Linq
open System.Diagnostics
open System.Threading
open System.Collections.Generic
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Runtime
open Aardvark.Base.Incremental
open OpenTK.Graphics.OpenGL4
open Microsoft.FSharp.NativeInterop
open Aardvark.Rendering.GL




module RenderTasks =

    [<AbstractClass>]
    type AbstractOpenGlRenderTask(manager : ResourceManager, fboSignature : IFramebufferSignature, config : IMod<BackendConfiguration>, shareTextures : bool, shareBuffers : bool) =
        inherit AbstractRenderTask()
        let ctx = manager.Context
        let renderTaskLock = RenderTaskLock()
        let manager = ResourceManager(manager, Some (fboSignature, renderTaskLock), shareTextures, shareBuffers)
        let allBuffers = manager.DrawBufferManager.CreateConfig(fboSignature.ColorAttachments |> Map.toSeq |> Seq.map (snd >> fst) |> Set.ofSeq)
        let structureChanged = Mod.custom ignore
        let runtimeStats = NativePtr.alloc 1

        let mutable isDisposed = false
        let currentContext = Mod.init Unchecked.defaultof<ContextHandle>
        let contextHandle = NativePtr.alloc 1
        do NativePtr.write contextHandle 0n


        let scope =
            { 
                runtimeStats = runtimeStats
                currentContext = currentContext
                contextHandle = contextHandle
                drawBuffers = NativePtr.toNativeInt allBuffers.Buffers
                drawBufferCount = allBuffers.Count 
                usedTextureSlots = RefRef HRefSet.empty
                usedUniformBufferSlots = RefRef HRefSet.empty
                structuralChange = structureChanged
                task = RenderTask.empty
                tags = Map.empty
            }
            
        let beforeRender = new System.Reactive.Subjects.Subject<unit>()
        let afterRender = new System.Reactive.Subjects.Subject<unit>()
        
        member x.BeforeRender = beforeRender
        member x.AfterRender = afterRender

        member x.StructureChanged() =
            transact (fun () -> structureChanged.MarkOutdated())

        member private x.pushDebugOutput(token : AdaptiveToken) =
            let c = config.GetValue token
            match ContextHandle.Current with
                | Some ctx -> let oldState = ctx.DebugOutputEnabled // get manually tracked state of GL.IsEnabled EnableCap.DebugOutput
                              ctx.DebugOutputEnabled <- c.useDebugOutput
                              oldState
                | None -> Report.Warn("No active context handle in RenderTask.Run")
                          false
            
        member private x.popDebugOutput(token : AdaptiveToken, wasEnabled : bool) =
            match ContextHandle.Current with
                | Some ctx -> ctx.DebugOutputEnabled <- wasEnabled
                | None -> Report.Warn("Still no active context handle in RenderTask.Run")

        member private x.pushFbo (desc : OutputDescription) =
            let fbo = desc.framebuffer |> unbox<Framebuffer>

            let handle = fbo.Handle |> unbox<int> 

            if ExecutionContext.framebuffersSupported then
                GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, handle)
                GL.Check "could not bind framebuffer"
        
                // DepthMask, ColorMask, StencilMask are by default writing everything when creating an FBO
                
                fbo.Signature.Images |> Map.iter (fun index sem ->
                    match Map.tryFind sem desc.images with
                        | Some img ->
                            let tex = img.texture |> unbox<Texture>
                            GL.BindImageTexture(index, tex.Handle, img.level, false, img.slice, TextureAccess.ReadWrite, unbox (int tex.Format))
                        | None -> 
                            GL.ActiveTexture(int TextureUnit.Texture0 + index |> unbox)
                            GL.BindTexture(TextureTarget.Texture2D, 0)
                    )

            elif handle <> 0 then
                failwithf "cannot render to texture on this OpenGL driver"

            GL.Viewport(desc.viewport.Min.X, desc.viewport.Min.Y, desc.viewport.SizeX + 1, desc.viewport.SizeY + 1)
            GL.Check "could not set viewport"
            

        member private x.popFbo (desc : OutputDescription) =
            if ExecutionContext.framebuffersSupported then
                // execution of RenderObjects might change Color/Depth/StencilMask or DrawBuffers
                // Color/Depth/StencilMask are reset by epilog RenderObject
                // Reset of DrawBuffers is not possible as this is dependent on the FBO?
                // -> Reset DrawBuffers in popFbo
              
                let fbo = desc.framebuffer |> unbox<Framebuffer>
                if fbo.Handle = 0 then
                    GL.DrawBuffer(DrawBufferMode.BackLeft);
                else
                    let drawBuffers = Array.init desc.framebuffer.Signature.ColorAttachments.Count (fun index -> DrawBuffersEnum.ColorAttachment0 + unbox index)
                    GL.DrawBuffers(drawBuffers.Length, drawBuffers);


        abstract member ProcessDeltas : AdaptiveToken * RenderToken -> unit
        abstract member UpdateResources : AdaptiveToken * RenderToken -> unit
        abstract member Perform : AdaptiveToken * RenderToken * Framebuffer * OutputDescription -> unit
        abstract member Update :  AdaptiveToken * RenderToken -> unit
        abstract member Release2 : unit -> unit



        member x.Config = config
        member x.Context = ctx
        member x.Scope = { scope with task = x }
        member x.RenderTaskLock = renderTaskLock
        member x.ResourceManager = manager

        override x.PerformUpdate(token, t) =
            use ct = ctx.ResourceLock
            x.ProcessDeltas(token, t)
            x.UpdateResources(token, t)

            renderTaskLock.Run (fun () ->
                x.Update(token, t)
            )


        override x.Release() =
            if not isDisposed then
                isDisposed <- true
                currentContext.Outputs.Clear()
                x.Release2()
        override x.FramebufferSignature = Some fboSignature
        override x.Runtime = Some ctx.Runtime
        override x.Perform(token : AdaptiveToken, t : RenderToken, desc : OutputDescription) =
            
            GL.Check "[RenderTask.Run] Entry"

            let fbo = desc.framebuffer // TODO: fix outputdesc
            if not <| fboSignature.IsAssignableFrom fbo.Signature then
                failwithf "incompatible FramebufferSignature\nexpected: %A but got: %A" fboSignature fbo.Signature

            use __ = ctx.ResourceLock 
            if currentContext.UnsafeCache <> ctx.CurrentContextHandle.Value then
                let intCtx = ctx.CurrentContextHandle.Value.Handle |> unbox<OpenTK.Graphics.IGraphicsContextInternal>
                NativePtr.write contextHandle intCtx.Context.Handle
                transact (fun () -> Mod.change currentContext ctx.CurrentContextHandle.Value)

            let fbo =
                match fbo with
                    | :? Framebuffer as fbo -> fbo
                    | _ -> failwithf "unsupported framebuffer: %A" fbo

            x.ProcessDeltas(token, t)
            x.UpdateResources(token, t)

            let debugState = x.pushDebugOutput(token)
            x.pushFbo desc

            renderTaskLock.Run (fun () ->
                beforeRender.OnNext()
                NativePtr.write runtimeStats V2i.Zero
                let stats = x.Perform(token, t, fbo, desc)
                GL.Check "[RenderTask.Run] Perform"
                afterRender.OnNext()
                let rt = NativePtr.read runtimeStats
                t.AddDrawCalls(rt.X, rt.Y)
            )

            x.popFbo desc
            x.popDebugOutput(token, debugState)
                            
            GL.BindVertexArray 0
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer,0)

    [<AbstractClass>]
    type AbstractSubTask() =
        static let nop = System.Lazy<unit>(id)

        let programUpdateWatch  = Stopwatch()
        let sortWatch           = Stopwatch()
        //let runWatch            = OpenGlStopwatch()

        let fragments = HashSet<RenderFragment>()

        member x.ProgramUpdate (t : RenderToken, f : unit -> 'a) =
            if RenderToken.isEmpty t then
                f()
            else
                programUpdateWatch.Restart()
                let res = f()
                programUpdateWatch.Stop()
                res

        member x.Sorting (t : RenderToken, f : unit -> 'a) =
            if RenderToken.isEmpty t then
                f()
            else
                sortWatch.Restart()
                let res = f()
                sortWatch.Stop()
                res

        member x.Execution (t : RenderToken, f : unit -> 'a) =
            f()
                
        abstract member Update : AdaptiveToken * RenderToken -> unit
        abstract member Perform : AdaptiveToken * RenderToken -> unit
        abstract member Dispose : unit -> unit
        abstract member Add : PreparedCommand -> unit
        abstract member Remove : PreparedCommand -> unit

        member x.Add(t : RenderFragment) = 
            fragments.Add t |> ignore

        member x.Remove(t : RenderFragment) = 
            fragments.Remove t |> ignore


        member x.Run(token : AdaptiveToken, t : RenderToken, output : OutputDescription) =

            for task in fragments do
                task.Run(token, t, output)

            x.Perform(token, t)
            if RenderToken.isEmpty t then
                nop
            else
                lazy (
                    t.AddSubTask(
                        MicroTime sortWatch.Elapsed,
                        MicroTime programUpdateWatch.Elapsed,
                        MicroTime.Zero, MicroTime.Zero
                        //runWatch.ElapsedGPU,
                        //runWatch.ElapsedCPU
                    )
                )

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    type NativeRenderProgram(cmp : IComparer<PreparedCommand>, scope : CompilerInfo, content : aset<PreparedCommand>, debug : bool) =
        inherit NativeProgram<PreparedCommand, NativeStats>(
                        ASet.sortWith (curry cmp.Compare) content, 
                        (fun l r s -> 
                                let asm =  AssemblerCommandStream(s) :> ICommandStream
                                let stream = if debug then DebugCommandStream(asm) :> ICommandStream else asm
                                r.Compile(scope, stream, l)
                            ),
                        NativeStats.Zero, (+), (-))
        
        let mutable stats = NativeProgramUpdateStatistics.Zero
        member x.Count = stats.Count

        member private x.UpdateInt(t) =
            let s = x.Update(t)
            if s <> NativeProgramUpdateStatistics.Zero then
                stats <- s


        interface IAdaptiveProgram<unit> with
            member x.Disassemble() = null
            member x.Run(_) = x.Run()
            member x.Update(t) = x.UpdateInt(t); AdaptiveProgramStatistics.Zero
            member x.StartDefragmentation() = Threading.Tasks.Task.FromResult(TimeSpan.Zero)
            member x.AutoDefragmentation
                with get() = false
                and set _ = ()
            member x.FragmentCount = 10
            member x.NativeCallCount = 10
            member x.ProgramSizeInBytes = 10L
            member x.TotalJumpDistanceInBytes = 10L
            


    type StaticOrderSubTask(ctx : Context, scope : CompilerInfo, config : IMod<BackendConfiguration>) =
        inherit AbstractSubTask()
        let objects : cset<PreparedCommand> = CSet.ofList [new EpilogCommand(ctx)]

        let mutable hasProgram = false
        let mutable currentConfig = BackendConfiguration.Default
        let mutable program : NativeRenderProgram = Unchecked.defaultof<_>


        let reinit (self : StaticOrderSubTask) (config : BackendConfiguration) =
            // if the config changed or we never compiled a program
            // we need to do something
            if not hasProgram then

                // if we have a program we'll dispose it now
                if hasProgram then program.Dispose()

                // use the config to create a comparer for IRenderObjects
                let comparer =
                    { new IComparer<PreparedCommand> with 
                        member x.Compare(l, r) =
                            match l, r with
                                | :? EpilogCommand, :? EpilogCommand -> compare l.Id r.Id
                                | :? EpilogCommand, _ -> 1
                                | _, :? EpilogCommand -> -1
                                | _ -> 
                                    match l.EntryState, r.EntryState with
                                        | None, None -> compare l.Id r.Id
                                        | None, Some _ -> -1
                                        | Some _, None -> 1
                                        | Some ls, Some rs ->
                                            let cmp = compare ls.pProgram.Id rs.pProgram.Id
                                            if cmp <> 0 then cmp
                                            else // efficient texture sorting requires that slots are ordered [Global, PerMaterial, PerInstance] -> currently alphabetic based on SamplerName !
                                                let mutable cmp = 0
                                                let mutable i = 0
                                                let texCnt = min ls.pTextureBindings.Length rs.pTextureBindings.Length
                                                while cmp = 0 && i < texCnt do
                                                    let struct (sltl, bndl) = ls.pTextureBindings.[i]
                                                    let struct (sltr, bndr) = ls.pTextureBindings.[i]
                                                    if (sltl <> sltr) then
                                                        cmp <- compare l.Id r.Id
                                                    else 
                                                        let leftTexId = match bndl with | ArrayBinding ab -> ab.Id; | SingleBinding (t, s) -> t.Id
                                                        let rigthTexId = match bndr with | ArrayBinding ab -> ab.Id; | SingleBinding (t, s) -> t.Id
                                                        cmp <- compare leftTexId rigthTexId
                                                    i <- i + 1
                                                if cmp <> 0 then cmp
                                                else compare l.Id r.Id
                    }

                // create the new program
                let newProgram = 
                    Log.line "using optimized native program"
                    new NativeRenderProgram(comparer, scope, objects, config.execution = ExecutionEngine.Debug) 


                // finally we store the current config/ program and set hasProgram to true
                program <- newProgram
                hasProgram <- true
                currentConfig <- config

        override x.Update(token, t) =
            let config = config.GetValue token
            reinit x config

            //TODO
            let programStats = x.ProgramUpdate (t, fun () -> program.Update AdaptiveToken.Top)
            ()

        override x.Perform(token, t) =
            x.Update(token, t) |> ignore
            x.Execution (t, fun () -> program.Run())
            let ic = program.Stats.InstructionCount
            t.AddInstructions(ic, 0) // don't know active
               

        override x.Dispose() =
            if hasProgram then
                hasProgram <- false
                program.Dispose()

                (objects :> aset<_>).Content.Outputs.Clear()

                objects.Clear()
        
        override x.Add(o) = 
            transact (fun () -> 
                scope.structuralChange.MarkOutdated()
                objects.Add o |> ignore
            )

        override x.Remove(o) = 
            transact (fun () -> 
                scope.structuralChange.MarkOutdated()
                objects.Remove o |> ignore
            )

    type RenderTask(man : ResourceManager, fboSignature : IFramebufferSignature, objects : aset<IRenderObject>, config : IMod<BackendConfiguration>, shareTextures : bool, shareBuffers : bool) as this =
        inherit AbstractOpenGlRenderTask(man, fboSignature, config, shareTextures, shareBuffers)
        
        let ctx = man.Context
        let resources = new Aardvark.Base.Rendering.ResourceInputSet()
        let inputSet = InputSet(this) 
        //let resourceUpdateWatch = OpenGlStopwatch()
        let structuralChange = Mod.init ()
        
        let primitivesGenerated = OpenGlQuery(QueryTarget.PrimitivesGenerated)
        
        let add (ro : PreparedCommand) = 
            let all = ro.Resources
            for r in all do resources.Add(r)
            ro.AddCleanup(fun () -> for r in all do resources.Remove r)
            ro
            
        let rec hook (r : IRenderObject) =
            match r with
                | :? RenderObject as o -> this.HookRenderObject o :> IRenderObject
                | :? MultiRenderObject as o -> MultiRenderObject(o.Children |> List.map hook) :> IRenderObject
                | _ -> r

        let preparedObjects = objects |> ASet.mapUse (hook >> PreparedCommand.ofRenderObject fboSignature this.ResourceManager >> add)
        let preparedObjectReader = preparedObjects.GetReader()

        let mutable subtasks = Map.empty

        let getSubTask (pass : RenderPass) : AbstractSubTask =
            match Map.tryFind pass subtasks with
                | Some task -> task
                | _ ->
                    let task = 
                        match pass.Order with
                            | RenderPassOrder.Arbitrary ->
                                new StaticOrderSubTask(ctx, this.Scope, this.Config) :> AbstractSubTask

                            | order ->
                                Log.warn "[GL] no sorting"
                                new StaticOrderSubTask(ctx, this.Scope, this.Config) :> AbstractSubTask //new CameraSortedSubTask(order, this) :> AbstractSubTask

                    subtasks <- Map.add pass task subtasks
                    task

        let processDeltas (x : AdaptiveToken) (parent : AbstractOpenGlRenderTask) (t : RenderToken) =
            
            let sw = Stopwatch.StartNew()

            let deltas = preparedObjectReader.GetOperations x

            if not (HDeltaSet.isEmpty deltas) then
                parent.StructureChanged()

            let mutable added = 0
            let mutable removed = 0
            for d in deltas do 
                match d with
                    | Add(_,v) ->
                        let task = getSubTask v.Pass
                        added <- added + 1
                        task.Add v

                    | Rem(_,v) ->
                        let task = getSubTask v.Pass
                        removed <- removed + 1
                        task.Remove v      
                        
            if added > 0 || removed > 0 then
                Log.line "[GL] RenderObjects: +%d/-%d (%dms)" added removed sw.ElapsedMilliseconds
            t.RenderObjectDeltas(added, removed)

        let updateResources (x : AdaptiveToken) (t : RenderToken) =
            if RenderToken.isEmpty t then
                resources.Update(x, t)
            else
                //resourceUpdateWatch.Restart()
                resources.Update(x, t)
                //resourceUpdateWatch.Stop()

                //t.AddResourceUpdate(resourceUpdateWatch.ElapsedCPU, resourceUpdateWatch.ElapsedGPU)


        override x.ProcessDeltas(token, t) =
            processDeltas token x t

        override x.UpdateResources(token,t) =
            updateResources token t

        override x.Perform(token : AdaptiveToken, rt : RenderToken, fbo : Framebuffer, output : OutputDescription) =
            x.ResourceManager.DrawBufferManager.Write(fbo)

            if not RuntimeConfig.SupressGLTimers && RenderToken.isValid rt then
                primitivesGenerated.Restart()

            let mutable runStats = []
            for (_,t) in Map.toSeq subtasks do
                let s = t.Run(token,rt, output)
                runStats <- s::runStats

            if RuntimeConfig.SyncUploadsAndFrames then
                GL.Sync()
            
            if not RuntimeConfig.SupressGLTimers && RenderToken.isValid rt then 
                primitivesGenerated.Stop()
                runStats |> List.iter (fun l -> l.Value)
                rt.AddPrimitiveCount(primitivesGenerated.Value)

        override x.Update(token, rt) = 
            for (_,t) in Map.toSeq subtasks do
                t.Update(token, rt)

        override x.Release2() =
            for ro in preparedObjectReader.State do
                ro.Dispose()

            preparedObjectReader.Dispose()
            resources.Dispose() // should be 0 after disposing all RenderObjects
            for (_,t) in Map.toSeq subtasks do
                t.Dispose()

            // UniformBufferManager should have 0 allocated blocks
            x.ResourceManager.Release()

            subtasks <- Map.empty

        override x.Use (f : unit -> 'a) =
            lock x (fun () ->
                x.RenderTaskLock.Run (fun () ->
                    lock resources (fun () ->
                        f()
                    )
                )
            )

    type ClearTask(runtime : IRuntime, fboSignature : IFramebufferSignature, color : IMod<list<Option<C4f>>>, depth : IMod<Option<float>>, ctx : Context) =
        inherit AbstractRenderTask()

        override x.PerformUpdate(token, t) = ()
        override x.Perform(token : AdaptiveToken, t : RenderToken, desc : OutputDescription) =
            let fbo = desc.framebuffer
            using ctx.ResourceLock (fun _ ->

                let old = Array.create 4 0
                let mutable oldFbo = 0
                OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.Viewport, old)
                OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo)

                let handle = fbo.GetHandle null |> unbox<int>

                if ExecutionContext.framebuffersSupported then
                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, handle)
                    GL.Check "could not bind framebuffer"
                elif handle <> 0 then
                    failwithf "cannot render to texture on this OpenGL driver"

                GL.Viewport(0, 0, fbo.Size.X, fbo.Size.Y)
                GL.Check "could not bind framebuffer"

                

                let depthValue = depth.GetValue token
                let colorValues = color.GetValue token
                    
                colorValues |> List.iteri (fun i _ ->
                    GL.ColorMask(i, true, true, true, true)
                )
                GL.DepthMask(true)
                GL.StencilMask(0xFFFFFFFFu)

                match colorValues, depthValue with
                    | [Some c], Some depth ->
                        GL.ClearColor(c.R, c.G, c.B, c.A)
                        GL.ClearDepth(depth)
                        GL.Clear(ClearBufferMask.ColorBufferBit ||| ClearBufferMask.DepthBufferBit ||| ClearBufferMask.StencilBufferBit)
                        
                    | [Some c], None ->
                        GL.ClearColor(c.R, c.G, c.B, c.A)
                        GL.Clear(ClearBufferMask.ColorBufferBit)

                    | l, Some depth when List.forall Option.isNone l ->
                        GL.ClearDepth(depth)
                        GL.Clear(ClearBufferMask.DepthBufferBit ||| ClearBufferMask.StencilBufferBit)
                    | l, d ->
                            
                        let mutable i = 0
                        for c in l do
                            match c with
                                | Some c ->
                                    GL.DrawBuffer(int DrawBufferMode.ColorAttachment0 + i |> unbox)
                                    GL.ClearColor(c.R, c.G, c.B, c.A)
                                    GL.Clear(ClearBufferMask.ColorBufferBit)
                                | None ->
                                    ()
                            i <- i + 1

                        match d with
                            | Some depth -> 
                                GL.ClearDepth(depth)
                                GL.Clear(ClearBufferMask.DepthBufferBit ||| ClearBufferMask.StencilBufferBit)
                            | None ->
                                ()


                if ExecutionContext.framebuffersSupported then
                    GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, oldFbo)

                GL.Viewport(old.[0], old.[1], old.[2], old.[3])
                GL.Check "could not bind framebuffer"
            )

        override x.Release() =
            color.RemoveOutput x
            depth.RemoveOutput x
        override x.FramebufferSignature = fboSignature |> Some
        override x.Runtime = runtime |> Some

        override x.Use f = lock x f
