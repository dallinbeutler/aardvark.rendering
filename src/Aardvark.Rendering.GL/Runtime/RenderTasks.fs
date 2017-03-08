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
open Aardvark.Rendering.GL.Compiler
open System.Runtime.CompilerServices
open Microsoft.FSharp.NativeInterop

module RenderTasks =
    open System.Collections.Generic


    [<AbstractClass>]
    type AbstractOpenGlRenderTask(manager : ResourceManager, fboSignature : IFramebufferSignature, config : IMod<BackendConfiguration>, shareTextures : bool, shareBuffers : bool) =
        inherit AbstractRenderTask()
        let ctx = manager.Context
        let renderTaskLock = RenderTaskLock()
        let manager = ResourceManager(manager.Context, Some (fboSignature, renderTaskLock), shareTextures, shareBuffers)
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
                usedTextureSlots = ref RefSet.empty
                usedUniformBufferSlots = ref RefSet.empty
                structuralChange = structureChanged
            }

//        let drawBuffers = 
//            fboSignature.ColorAttachments 
//                |> Map.toList 
//                |> List.map (fun (i,_) -> int DrawBuffersEnum.ColorAttachment0 + i |> unbox<DrawBuffersEnum>)
//                |> List.toArray
        
        let beforeRender = new System.Reactive.Subjects.Subject<unit>()
        let afterRender = new System.Reactive.Subjects.Subject<unit>()

        member x.BeforeRender = beforeRender
        member x.AfterRender = afterRender

        member x.StructureChanged() =
            transact (fun () -> structureChanged.MarkOutdated())

        member private x.pushDebugOutput(token : AdaptiveToken) =
            let wasEnabled = GL.IsEnabled EnableCap.DebugOutput
            let c = config.GetValue token
            if c.useDebugOutput then
                match ContextHandle.Current with
                    | Some v -> v.AttachDebugOutputIfNeeded()
                    | None -> Report.Warn("No active context handle in RenderTask.Run")
                GL.Enable EnableCap.DebugOutput

            wasEnabled

        member private x.popDebugOutput(token : AdaptiveToken, wasEnabled : bool) =
            let c = config.GetValue token
            if wasEnabled <> c.useDebugOutput then
                if wasEnabled then GL.Enable EnableCap.DebugOutput
                else GL.Disable EnableCap.DebugOutput

        member private x.pushFbo (desc : OutputDescription) =
            let fbo = desc.framebuffer |> unbox<Framebuffer>
            let old = Array.create 4 0
            let mutable oldFbo = 0
            OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.Viewport, old)
            OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo)

            let handle = fbo.Handle |> unbox<int> 

            if ExecutionContext.framebuffersSupported then
                GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, handle)
                GL.Check "could not bind framebuffer"
        
                GL.DepthMask(true)
                GL.StencilMask(0xFFFFFFFFu)
                
                for (index,(sem,_)) in fbo.Signature.ColorAttachments |> Map.toSeq do
                    GL.ColorMask(index, true, true, true, true)

                for (index, sem) in fbo.Signature.Images |> Map.toSeq do
                    match Map.tryFind sem desc.images with
                        | Some img ->
                            let tex = img.texture |> unbox<Texture>
                            GL.BindImageTexture(index, tex.Handle, img.level, false, img.slice, TextureAccess.ReadWrite, unbox (int tex.Format))
                        | None -> 
                            GL.ActiveTexture(int TextureUnit.Texture0 + index |> unbox)
                            GL.BindTexture(TextureTarget.Texture2D, 0)

            elif handle <> 0 then
                failwithf "cannot render to texture on this OpenGL driver"

            GL.Viewport(desc.viewport.Min.X, desc.viewport.Min.Y, desc.viewport.SizeX, desc.viewport.SizeY)
            GL.Check "could not set viewport"

       

            oldFbo, old

        member private x.popFbo (desc : OutputDescription, (oldFbo : int, old : int[])) =
            if ExecutionContext.framebuffersSupported then
                GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, oldFbo)
                GL.Check "could not bind framebuffer"

                for (index, sem) in desc.framebuffer.Signature.Images |> Map.toSeq do
                    GL.ActiveTexture(int TextureUnit.Texture0 + index |> unbox)
                    GL.BindTexture(TextureTarget.Texture2D, 0)


            GL.Viewport(old.[0], old.[1], old.[2], old.[3])
            GL.Check "could not set viewport"

        abstract member ProcessDeltas : AdaptiveToken * RenderToken -> unit
        abstract member UpdateResources : AdaptiveToken * RenderToken -> unit
        abstract member Perform : AdaptiveToken * RenderToken * Framebuffer -> unit
        abstract member Release : unit -> unit



        member x.Config = config
        member x.Context = ctx
        member x.Scope = scope
        member x.RenderTaskLock = renderTaskLock
        member x.ResourceManager = manager

        override x.PerformUpdate(token, t) =
            use ct = ctx.ResourceLock
            x.ProcessDeltas(token, t)
            x.UpdateResources(token, t)

        override x.Dispose() =
            if not isDisposed then
                isDisposed <- true
                let dummy = ref 0
                currentContext.Outputs.Consume(dummy) |> ignore
                x.Release()
        override x.FramebufferSignature = Some fboSignature
        override x.Runtime = Some ctx.Runtime
        override x.Perform(token : AdaptiveToken, t : RenderToken, desc : OutputDescription) =
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


            let debugState = x.pushDebugOutput(token)
            let fboState = x.pushFbo desc

            x.ProcessDeltas(token, t)
            x.UpdateResources(token, t)

            renderTaskLock.Run (fun () ->
                beforeRender.OnNext()
                NativePtr.write runtimeStats V2i.Zero
                let stats = x.Perform(token, t, fbo)
                GL.Check "RenderTask.Run"
                afterRender.OnNext()
                let rt = NativePtr.read runtimeStats
                t.AddDrawCalls(rt.X, rt.Y)
            )

            x.popFbo (desc, fboState)
            x.popDebugOutput(token, debugState)

                
            GL.BindVertexArray 0
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer,0)
            

            

    [<AbstractClass>]
    type AbstractSubTask(parent : AbstractRenderTask) =
        static let nop = System.Lazy<unit>(id)

        let programUpdateWatch  = Stopwatch()
        let sortWatch           = Stopwatch()
        let runWatch            = OpenGlStopwatch()

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
            if RenderToken.isEmpty t then
                f()
            else
                runWatch.Restart()
                let res = f()
                runWatch.Stop()
                res

        member x.Parent = parent

        abstract member Update : AdaptiveToken * RenderToken -> unit
        abstract member Perform : AdaptiveToken * RenderToken -> unit
        abstract member Dispose : unit -> unit
        abstract member Add : PreparedMultiRenderObject -> unit
        abstract member Remove : PreparedMultiRenderObject -> unit


        member x.Run(token : AdaptiveToken, t : RenderToken) =
            x.Perform(token, t)
            if RenderToken.isEmpty t then
                nop
            else
                lazy (
                    t.AddSubTask(
                        MicroTime sortWatch.Elapsed,
                        MicroTime programUpdateWatch.Elapsed,
                        runWatch.ElapsedGPU,
                        runWatch.ElapsedCPU
                    )
                )

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    type SortKey = list<int>

    type ProjectionComparer(projections : list<RenderObject -> IMod>) =

        let rec getRenderObject (ro : IRenderObject) =
            match ro with
                | :? RenderObject as ro -> ro
                | :? MultiRenderObject as ro -> ro.Children |> List.head |> getRenderObject
                | :? PreparedRenderObject as ro -> ro.Original
                | :? PreparedMultiRenderObject as ro -> ro.First.Original
                | _ -> failwithf "[ProjectionComparer] unknown RenderObject: %A" ro

        let ids = ConditionalWeakTable<IMod, ref<int>>()
        let mutable currentId = 0
        let getId (m : IMod) =
            match ids.TryGetValue m with
                | (true, r) -> !r
                | _ ->
                    let id = Interlocked.Increment &currentId
                    ids.Add(m, ref id)
                    id

        let maxKey = Int32.MaxValue :: (projections |> List.map (fun _ -> Int32.MaxValue))

        let keys = ConditionalWeakTable<IRenderObject, SortKey>()
        let project (ro : IRenderObject) =
            let ro = getRenderObject ro

            match keys.TryGetValue ro with
                | (true, key) -> key
                | _ ->
                    if ro.Id < 0 then
                        maxKey
                    else
                        let key = projections |> List.map (fun p -> p ro |> getId)
                        keys.Add(ro, key)
                        key


        interface IComparer<IRenderObject> with
            member x.Compare(l : IRenderObject, r : IRenderObject) =
                let left = project l
                let right = project r
                compare left right

    type StaticOrderSubTask(parent : AbstractOpenGlRenderTask) =
        inherit AbstractSubTask(parent)
        static let empty = new PreparedMultiRenderObject([PreparedRenderObject.empty])
        let objects = CSet.ofList [empty]

        let mutable hasProgram = false
        let mutable currentConfig = BackendConfiguration.Default
        let mutable program : IRenderProgram = Unchecked.defaultof<_>
        let structuralChange = Mod.custom ignore
        let scope = { parent.Scope with structuralChange = structuralChange }

        // TODO: add AdaptiveProgram creator not taking a separate key but simply comparing the values
        let objectsWithKeys = objects |> ASet.map (fun o -> (o :> IRenderObject, o))

        let reinit (self : StaticOrderSubTask) (config : BackendConfiguration) =
            // if the config changed or we never compiled a program
            // we need to do something
            if config <> currentConfig || not hasProgram then

                // if we have a program we'll dispose it now
                if hasProgram then program.Dispose()

                // use the config to create a comparer for IRenderObjects
                let comparer =
                    match config.sorting with
                        | RenderObjectSorting.Grouping projections -> 
                            ProjectionComparer(projections) :> IComparer<_>

                        | RenderObjectSorting.Static comparer -> 
                            { new IComparer<_> with 
                                member x.Compare(l, r) =
                                    if l.Id = r.Id then 0
                                    elif l.Id < 0 then -1
                                    elif r.Id < 0 then 1
                                    else comparer.Compare(l,r)
                            }

                        | Arbitrary ->
                            { new IComparer<_> with 
                                member x.Compare(l, r) =
                                    if l.Id < 0 then -1
                                    elif r.Id < 0 then 1
                                    else 0
                            }

                        | RenderObjectSorting.Dynamic create ->
                            failwith "[AbstractRenderTask] dynamic sorting not implemented"

                // create the new program
                let newProgram = 
                    match config.execution, config.redundancy with
                        | ExecutionEngine.Interpreter, _ ->
                            Log.line "using interpreted program"
                            RenderProgram.Interpreter.runtime scope objects

                        | ExecutionEngine.Native, RedundancyRemoval.Static -> 
                            Log.line "using optimized native program"
                            RenderProgram.Native.optimized scope comparer objectsWithKeys

                        | ExecutionEngine.Native, RedundancyRemoval.None -> 
                            Log.line "using unoptimized native program"
                            RenderProgram.Native.unoptimized scope comparer objectsWithKeys

                        | ExecutionEngine.Managed, RedundancyRemoval.Static -> 
                            Log.line "using optimized managed program"
                            RenderProgram.Managed.optimized scope comparer objectsWithKeys

                        | ExecutionEngine.Managed, RedundancyRemoval.None -> 
                            Log.line "using unoptimized managed program"
                            RenderProgram.Managed.unoptimized scope comparer objectsWithKeys

                        | ExecutionEngine.Debug, RedundancyRemoval.Static -> 
                            Log.line "using optimized debug program"
                            RenderProgram.Debug.optimized scope comparer objectsWithKeys

                        | ExecutionEngine.Debug, RedundancyRemoval.None -> 
                            Log.line "using unoptimized debug program"
                            RenderProgram.Debug.unoptimized scope comparer objectsWithKeys


                        | ExecutionEngine.Unmanaged, RedundancyRemoval.Static -> 
                            Log.line "using optimized unmanaged program"
                            RenderProgram.GLVM.optimized scope comparer objectsWithKeys

                        | ExecutionEngine.Unmanaged, RedundancyRemoval.Runtime -> 
                            Log.line "using runtime-optimized unmanaged program"
                            RenderProgram.GLVM.runtime scope comparer objectsWithKeys

                        | ExecutionEngine.Unmanaged, RedundancyRemoval.None -> 
                            Log.line "using unoptimized unmanaged program"
                            RenderProgram.GLVM.unoptimized scope comparer objectsWithKeys

                        | t ->
                            failwithf "[GL] unsupported backend configuration: %A" t


                // finally we store the current config/ program and set hasProgram to true
                program <- newProgram
                hasProgram <- true
                currentConfig <- config

        override x.Update(token, t) =
            let config = parent.Config.GetValue token
            reinit x config

            //TODO
            let programStats = x.ProgramUpdate (t, fun () -> program.Update AdaptiveToken.Top)
            ()
        override x.Perform(token, t) =
            x.Update(token, t) |> ignore

            let stats = x.Execution (t, fun () -> program.Run(t))

            stats
               

        override x.Dispose() =
            if hasProgram then
                hasProgram <- false
                program.Dispose()

                let mutable foo = 0
                (objects :> aset<_>).Content.Outputs.Consume(&foo) |> ignore

                objects.Clear()
        
        override x.Add(o) = 
            transact (fun () -> 
                structuralChange.MarkOutdated()
                objects.Add o |> ignore
            )

        override x.Remove(o) = 
            transact (fun () -> 
                structuralChange.MarkOutdated()
                objects.Remove o |> ignore
            )


    

                
    [<AllowNullLiteral>]
    type AdaptiveGLVMFragment(obj : PreparedMultiRenderObject, adaptiveCode : IAdaptiveCode<Instruction>) =
        inherit AdaptiveObject()

        let boundingBox : IMod<Box3d> =
            if obj.First.Id < 0 then Mod.constant Box3d.Invalid
            else
                match Ag.tryGetAttributeValue obj.First.Original.AttributeScope "GlobalBoundingBox" with
                    | Success box -> box
                    | _ -> failwith "[GL] could not get BoundingBox for RenderObject"
        let mutable currentBox = Box3d.Invalid

        let mutable prev : AdaptiveGLVMFragment = null
        let mutable next : AdaptiveGLVMFragment = null

        let code = List.toArray adaptiveCode.Content
        let frag = GLVM.vmCreate()
        let blocksWithContent = code |> Array.map (fun content -> (GLVM.vmNewBlock frag, content))

        let blockTable =
            code 
                |> Array.mapi (fun i m ->
                    if m.IsConstant then 
                        None
                    else
                        Some (m :> IAdaptiveObject, blocksWithContent.[i])
                   )
                |> Array.choose id
                |> Dictionary.ofArray

        let getArgs (o : Instruction) =
            o.Arguments |> Array.map (fun arg ->
                match arg with
                    | :? int as i -> nativeint i
                    | :? int64 as i -> nativeint i
                    | :? nativeint as i -> i
                    | :? float32 as f -> BitConverter.ToInt32(BitConverter.GetBytes(f), 0) |> nativeint
                    | :? PtrArgument as p ->
                        match p with
                            | Ptr32 p -> p
                            | Ptr64 p -> p
                    | _ -> failwith "invalid argument"
            )

        let writeBlock (id : int) (instructions : seq<Instruction>) =
            GLVM.vmClearBlock(frag, id)
            for i in instructions do
                match getArgs i with
                    | [| a |] -> GLVM.vmAppend1(frag, id, int i.Operation, a)
                    | [| a; b |] -> GLVM.vmAppend2(frag, id, int i.Operation, a, b)
                    | [| a; b; c |] -> GLVM.vmAppend3(frag, id, int i.Operation, a, b, c)
                    | [| a; b; c; d |] -> GLVM.vmAppend4(frag, id, int i.Operation, a, b, c, d)
                    | [| a; b; c; d; e |] -> GLVM.vmAppend5(frag, id, int i.Operation, a, b, c, d, e)
                    | [| a; b; c; d; e; f |] -> GLVM.vmAppend6(frag, id, int i.Operation, a, b, c, d, e, f)
                    | _ -> failwithf "invalid instruction: %A" i

        let dirtyBlocks = HashSet blocksWithContent
        
        override x.InputChanged (transaction : obj, o : IAdaptiveObject) =
            match blockTable.TryGetValue o with
                | (true, dirty) -> lock dirtyBlocks (fun () -> dirtyBlocks.Add dirty |> ignore)
                | _ -> ()

        member x.Object = obj

        member x.BoundingBox = currentBox

        member x.Update(token : AdaptiveToken, rt : RenderToken) =
            x.EvaluateAlways token (fun token ->
                if x.OutOfDate then
                    let blocks = 
                        lock dirtyBlocks (fun () ->
                            let all = Seq.toList dirtyBlocks
                            dirtyBlocks.Clear()
                            all
                        )

                    for (block, content) in blocks do
                        let c = content.GetValue token
                        writeBlock block c

                    currentBox <- boundingBox.GetValue token

            )

        member x.Handle = frag

        member x.Next
            with get() = next
            and set v = 
                next <- v
                if isNull v then GLVM.vmLink(frag, 0n)
                else GLVM.vmLink(frag, v.Handle)

        member x.Prev
            with get() = prev
            and set v = prev <- v

        member x.Dispose() =
            adaptiveCode.Dispose()
            if not (isNull prev) then prev.Next <- next
            if not (isNull next) then next.Prev <- prev
            GLVM.vmDelete frag

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    type SortedGLVMProgram(parent : CameraSortedSubTask, objects : aset<PreparedMultiRenderObject>, createComparer : Ag.Scope -> IMod<IComparer<PreparedMultiRenderObject>>) =
        inherit AbstractRenderProgram<AdaptiveGLVMFragment>()
        
        static let empty = new PreparedMultiRenderObject([PreparedRenderObject.empty])
        static let mutable initialized = false
        do if not initialized then
            initialized <- true
            GLVM.vmInit()
        
        let fragments = objects |> ASet.mapUse (fun o -> new AdaptiveGLVMFragment(o, RenderProgram.Compiler.compileFull parent.Scope o))
        let fragmentReader = fragments.GetReader()
        let mutable vmStats = VMStats()
        let last = new AdaptiveGLVMFragment(empty, RenderProgram.Compiler.compileFull parent.Scope empty)
        let mutable first : AdaptiveGLVMFragment = last

        let mutable comparer = None

        let mutable disposeCnt = 0

        let getComparer (f : seq<AdaptiveGLVMFragment>) =
            match comparer with
                | Some cmp -> cmp
                | None ->
                    if Seq.isEmpty f then
                        Mod.constant { new IComparer<_> with member x.Compare(a,b) = 0 }
                    else
                        let fst = Seq.head f
                        let c = createComparer fst.Object.Original.AttributeScope
                        comparer <- Some c
                        c


        member private x.sort (token : AdaptiveToken, f : seq<AdaptiveGLVMFragment>) : list<AdaptiveGLVMFragment> =
            let comparer = getComparer f
            let cmp = comparer.GetValue token
            f |> Seq.sortWith (fun a b -> cmp.Compare(a.Object, b.Object)) |> Seq.toList

        override x.Update(token : AdaptiveToken, rt : RenderToken, dirty : HashSet<_>) =
            let deltas = fragmentReader.GetOperations(AdaptiveToken.Top)
            for d in deltas do
                match d with
                    | Add(_,f) -> dirty.Add f |> ignore
                    | Rem(_,f) -> dirty.Remove f |> ignore

            for d in dirty do d.Update(token, rt)

            parent.Sorting (rt, fun () ->
                let ordered = x.sort(token, fragmentReader.State)

                let mutable current = null
                for f in ordered do
                    f.Prev <- current
                    if isNull current then first <- f
                    else current.Next <- f
                    current <- f

                if not <| isNull current then current.Next <- last
                else first <- last
            )

        override x.Run(t) =
            
            if disposeCnt > 0 then
                failwithf "Running disposed glvmprogram"

            vmStats.TotalInstructions <- 0
            vmStats.RemovedInstructions <- 0
            if not (isNull first) then
                last.Next <- null
                GLVM.vmRun(first.Handle, VMMode.RuntimeRedundancyChecks, &vmStats)

            t.AddInstructions(vmStats.TotalInstructions, vmStats.TotalInstructions - vmStats.RemovedInstructions)

        override x.Dispose() =
            if Interlocked.Increment &disposeCnt = 1 then
                last.Dispose()
                fragmentReader.Dispose()    
            else
                Log.warn "double dispose"

    and SortedInterpreterProgram(parent : CameraSortedSubTask, objects : aset<PreparedMultiRenderObject>, createComparer : Ag.Scope -> IMod<IComparer<PreparedMultiRenderObject>>) =
        inherit AbstractRenderProgram()

        let reader = objects.GetReader()
        let mutable arr = null

        let mutable comparer = None
        let mutable activeInstructions = 0
        let mutable totalInstructions = 0

        let getComparer (f : seq<PreparedMultiRenderObject>) =
            match comparer with
                | Some cmp -> cmp
                | None ->
                    if Seq.isEmpty f then
                        Mod.constant { new IComparer<_> with member x.Compare(a,b) = 0 }
                    else
                        let fst = Seq.head f
                        let c = createComparer fst.Original.AttributeScope
                        comparer <- Some c
                        c


        override x.PerformUpdate(token : AdaptiveToken, t : RenderToken) =
            reader.GetOperations(token) |> ignore

            parent.Sorting (t, fun () ->
                let comparer = getComparer reader.State
                let cmp = comparer.GetValue token
                arr <- reader.State |> Seq.sortWith (fun a b -> cmp.Compare(a,b)) |> Seq.toArray
            )

        override x.Run(t) =
            Interpreter.run parent.Scope.contextHandle (fun gl ->
                for a in arr do gl.render a

                t.AddInstructions(gl.TotalInstructions, gl.EffectiveInstructions)
            )

        override x.Dispose() =
            reader.Dispose()

    and CameraSortedSubTask(order : RenderPassOrder, parent : AbstractOpenGlRenderTask) =
        inherit AbstractSubTask(parent)
        do GLVM.vmInit()

        let structuralChange = Mod.custom ignore
        let scope = { parent.Scope with structuralChange = structuralChange }

        let mutable hasCameraView = false
        let mutable cameraView = Mod.constant Trafo3d.Identity
        
        let objects = CSet.empty
        let boundingBoxes = Dictionary<PreparedMultiRenderObject, IMod<Box3d>>()
        let mutable compareToken = AdaptiveToken.Top
        let bb (o : PreparedMultiRenderObject) =
            boundingBoxes.[o].GetValue(compareToken)

        let mutable program = Unchecked.defaultof<IRenderProgram>
        let mutable hasProgram = false
        let mutable currentConfig = BackendConfiguration.Debug

        let createComparer (scope : Ag.Scope) =
            Mod.custom (fun self ->
                let cam = cameraView.GetValue self
                let pos = cam.GetViewPosition()

                match order with
                    | RenderPassOrder.BackToFront ->
                        { new IComparer<PreparedMultiRenderObject> with
                            member x.Compare(l,r) = compare ((bb r).GetMinimalDistanceTo pos) ((bb l).GetMinimalDistanceTo pos)
                        }
                    | _ ->
                        { new IComparer<PreparedMultiRenderObject> with
                            member x.Compare(l,r) = compare ((bb l).GetMinimalDistanceTo pos) ((bb r).GetMinimalDistanceTo pos)
                        }
            )


        let reinit (self : CameraSortedSubTask) (c : BackendConfiguration) =
            if currentConfig <> c || not hasProgram then
                if hasProgram then
                    program.Dispose()

                let newProgram = 
                    match c.execution with
                        | ExecutionEngine.Interpreter -> new SortedInterpreterProgram(self, objects, createComparer) :> IRenderProgram
                        | _ -> new SortedGLVMProgram(self, objects, createComparer) :> IRenderProgram

                program <- newProgram
                hasProgram <- true
                currentConfig <- c

        member x.Scope = scope

        override x.Update(token, t) = 
            compareToken <- token
            let cfg = parent.Config.GetValue token
            reinit x cfg

            let updateStats = x.ProgramUpdate (t, fun () -> program.Update(AdaptiveToken.Top, t))
            ()

        override x.Perform(token, t) =
            compareToken <- token
            x.Update(token, t) |> ignore
            x.Execution (t, fun () -> program.Run(t))



        override x.Dispose() =
            if hasProgram then
                program.Dispose()
                hasProgram <- false

            objects.Clear()
            hasCameraView <- false
            cameraView <- Mod.constant Trafo3d.Identity
        
        override x.Add(o) = 
            if not hasCameraView then
                let o = o.First.Original

                match o.Uniforms.TryGetUniform (o.AttributeScope, Symbol.Create "ViewTrafo") with
                    | Some (:? IMod<Trafo3d> as view) -> 
                        hasCameraView <- true
                        cameraView <- view
                    | _ -> ()

            if o.First.Id < 0 then
                 boundingBoxes.[o] <- Mod.constant Box3d.Invalid
            else
                match Ag.tryGetAttributeValue o.Original.AttributeScope "GlobalBoundingBox" with
                    | Success b -> boundingBoxes.[o] <- b
                    | _ -> failwithf "[GL] could not get bounding-box for RenderObject"

            transact (fun () -> 
                structuralChange.MarkOutdated()
                objects.Add o |> ignore
            )

        override x.Remove(o) =
            boundingBoxes.Remove o |> ignore
            transact (fun () -> 
                structuralChange.MarkOutdated()
                objects.Remove o |> ignore
            )

                
    type RenderTask(man : ResourceManager, fboSignature : IFramebufferSignature, objects : aset<IRenderObject>, config : IMod<BackendConfiguration>, shareTextures : bool, shareBuffers : bool) as this =
        inherit AbstractOpenGlRenderTask(man, fboSignature, config, shareTextures, shareBuffers)
        
        let ctx = man.Context
        let resources = new Aardvark.Base.Rendering.ResourceInputSet()
        let inputSet = InputSet(this) 
        let resourceUpdateWatch = OpenGlStopwatch()
        let structuralChange = Mod.init ()
        
        let primitivesGenerated = OpenGlQuery(QueryTarget.PrimitivesGenerated)

        let vaoCache = ResourceCache(None, Some this.RenderTaskLock)

        let add (ro : PreparedRenderObject) = 
            let all = ro.Resources |> Seq.toList
            for r in all do resources.Add(r)

            
            let old = ro.Activation
            ro.Activation <- 
                { new IDisposable with
                    member x.Dispose() =
                        old.Dispose()
                        for r in all do resources.Remove r
                        //callStats.Remove ro
                        ro.Activation <- old
                }

            ro

        let rec prepareRenderObject (ro : IRenderObject) =
            match ro with
                | :? RenderObject as r ->
                    let hooked = this.HookRenderObject r 
                    new PreparedMultiRenderObject([this.ResourceManager.Prepare(fboSignature, hooked) |> add])

                | :? PreparedRenderObject as prep ->
                    new PreparedMultiRenderObject([prep |> PreparedRenderObject.clone |> add])

                | :? MultiRenderObject as seq ->
                    let all = seq.Children |> List.collect(fun o -> (prepareRenderObject o).Children)
                    new PreparedMultiRenderObject(all)

                | :? PreparedMultiRenderObject as seq ->
                    new PreparedMultiRenderObject (seq.Children |> List.map (PreparedRenderObject.clone >> add))

                | _ ->
                    failwithf "[RenderTask] unsupported IRenderObject: %A" ro

        let preparedObjects = objects |> ASet.mapUse prepareRenderObject
        let preparedObjectReader = preparedObjects.GetReader()

        let mutable subtasks = Map.empty

        let getSubTask (pass : RenderPass) : AbstractSubTask =
            match Map.tryFind pass subtasks with
                | Some task -> task
                | _ ->
                    let task = 
                        match pass.Order with
                            | RenderPassOrder.Arbitrary ->
                                new StaticOrderSubTask(this) :> AbstractSubTask

                            | order ->
                                new CameraSortedSubTask(order, this) :> AbstractSubTask

                    subtasks <- Map.add pass task subtasks
                    task

        let processDeltas (x : AdaptiveToken) (parent : AbstractOpenGlRenderTask) (t : RenderToken) =
            let deltas = preparedObjectReader.GetOperations x

            if not (HDeltaSet.isEmpty deltas) then
                parent.StructureChanged()

            let mutable added = 0
            let mutable removed = 0
            for d in deltas do 
                match d with
                    | Add(_,v) ->
                        let task = getSubTask v.RenderPass
                        added <- added + 1
                        task.Add v
                    | Rem(_,v) ->
                        let task = getSubTask v.RenderPass
                        removed <- removed + 1
                        task.Remove v            

            t.RenderObjectDeltas(added, removed)

        let updateResources (x : AdaptiveToken) (t : RenderToken) =
            if RenderToken.isEmpty t then
                resources.Update(x, t)
            else
                resourceUpdateWatch.Restart()
                resources.Update(x, t)
                resourceUpdateWatch.Stop()

                t.AddResourceUpdate(resourceUpdateWatch.ElapsedCPU, resourceUpdateWatch.ElapsedGPU)


        override x.ProcessDeltas(token, t) =
            processDeltas token x t

        override x.UpdateResources(token,t) =
            updateResources token t

        override x.Perform(token : AdaptiveToken, rt : RenderToken, fbo : Framebuffer) =
            x.ResourceManager.DrawBufferManager.Write(fbo)

            if not RuntimeConfig.SupressGLTimers && RenderToken.isValid rt then
                primitivesGenerated.Restart()

            let mutable runStats = []
            for (_,t) in Map.toSeq subtasks do
                let s = t.Run(token,rt)
                runStats <- s::runStats

            if RuntimeConfig.SyncUploadsAndFrames then
                GL.Sync()
            
            if not RuntimeConfig.SupressGLTimers && RenderToken.isValid rt then 
                primitivesGenerated.Stop()
                runStats |> List.iter (fun l -> l.Value)
                rt.AddPrimitiveCount(primitivesGenerated.Value)



        override x.Release() =
            preparedObjectReader.Dispose()
            resources.Dispose()
            for (_,t) in Map.toSeq subtasks do
                t.Dispose()

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

        override x.Dispose() =
            color.RemoveOutput x
            depth.RemoveOutput x
        override x.FramebufferSignature = fboSignature |> Some
        override x.Runtime = runtime |> Some

        override x.Use f = lock x f

