﻿namespace Aardvark.Rendering.GL

#nowarn "9"

open System
open Aardvark.Base.Incremental
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.ShaderReflection
open System.Runtime.CompilerServices
open Microsoft.FSharp.NativeInterop
open OpenTK.Graphics.OpenGL4
open Aardvark.Rendering.GL
open Aardvark.Base.Runtime
open FShade.GLSL
open System.Threading

[<AutoOpen>]
module private Hacky =

    let toBufferCache = 
        UnaryCache<IMod, IMod<IBuffer>>(fun m ->
            Mod.custom (fun t -> 
                match m.GetValue t with
                    | :? Array as a -> ArrayBuffer(a) :> IBuffer
                    | :? IBuffer as b -> b
                    | _ -> failwith "invalid storage buffer content"
            )
        )

type TextureBindingSlot = 
    | ArrayBinding of IResource<TextureBinding, TextureBinding>
    | SingleBinding of IResource<Texture, V2i> * IResource<Sampler, int>

type PreparedPipelineState =
    {
        pContext : Context

        pUniformProvider : IUniformProvider

        pFramebufferSignature : IFramebufferSignature
        pProgram : IResource<Program, int>
        pProgramInterface : GLSLProgramInterface
        pUniformBuffers : (struct (int * IResource<UniformBufferView, int>))[] // sorted list of uniform buffers
        pStorageBuffers : (struct (int * IResource<Buffer, int>))[] // sorted list of storage buffers
        pUniforms : (struct (int * IResource<UniformLocation, nativeint>))[] // sorted list of uniforms
        pTextureBindings : (struct (Range1i * TextureBindingSlot))[] // sorted list of texture bindings
                
        pDepthTestMode : IResource<DepthTestInfo, DepthTestInfo>
        pDepthBias : IResource<DepthBiasInfo, DepthBiasInfo>
        pCullMode : IResource<int, int>
        pFrontFace : IResource<int, int>
        pPolygonMode : IResource<int, int>
        pBlendMode : IResource<GLBlendMode, GLBlendMode>
        pStencilMode : IResource<GLStencilMode, GLStencilMode>
        pConservativeRaster : IResource<bool, int>
        pMultisample : IResource<bool, int>

        pColorAttachmentCount : int
        pDrawBuffers : Option<DrawBufferConfig>
        pColorBufferMasks : Option<list<V4i>>
        pDepthBufferMask : bool
        pStencilBufferMask : bool
        
    } 

    member x.Resources =
        seq {
            yield x.pProgram :> IResource
            for struct (_,b) in x.pUniformBuffers do
                yield b :> _
                
            for struct (_,b) in x.pStorageBuffers do
                yield b :> _

            for struct (_,u) in x.pUniforms do
                yield u :> _

            for struct (_, tb) in x.pTextureBindings do
                match tb with 
                | ArrayBinding ta -> yield ta :> _
                | SingleBinding (tex, sam) -> yield tex :> _; yield sam :> _
            
            yield x.pConservativeRaster :> _
            yield x.pMultisample :> _
            yield x.pDepthTestMode :> _
            yield x.pDepthBias :> _
            yield x.pCullMode :> _
            yield x.pFrontFace :> _
            yield x.pPolygonMode :> _
            yield x.pBlendMode :> _
            yield x.pStencilMode :> _
        }

    //member x.Update(caller : AdaptiveToken, token : RenderToken) =
    //    use ctxToken = x.pContext.ResourceLock

    //    x.pProgram.Update(caller, token)

    //    for (_,ub) in x.pUniformBuffers |> Map.toSeq do
    //        ub.Update(caller, token)
            
    //    for (_,ub) in x.pStorageBuffers |> Map.toSeq do
    //        ub.Update(caller, token)

    //    for (_,ul) in x.pUniforms |> Map.toSeq do
    //        ul.Update(caller, token)

    //    x.pTextures.Update(caller, token)
        
        
    //    x.pDepthTestMode.Update(caller, token)
    //    x.pCullMode.Update(caller, token)
    //    x.pPolygonMode.Update(caller, token)
    //    x.pBlendMode.Update(caller, token)
    //    x.pStencilMode.Update(caller, token)
    //    x.pConservativeRaster.Update(caller, token)
    //    x.pMultisample.Update(caller, token)

    member x.Dispose() =
        lock x (fun () -> 
            // ObjDisposed might occur here if GL is dead already and render objects get disposed nondeterministically by finalizer thread.
            let resourceLock = try Some x.pContext.ResourceLock with :? ObjectDisposedException as o -> None

            match resourceLock with
                | None ->
                    // OpenGL already dead
                    ()
                | Some l -> 
                    use resourceLock = l

                    OpenTK.Graphics.OpenGL4.GL.UnbindAllBuffers()

                    match x.pDrawBuffers with
                        | Some b -> b.RemoveRef()
                        | _ -> ()

                    for struct (_, tb) in x.pTextureBindings do
                        match tb with
                        | SingleBinding (tex, sam) -> tex.Dispose(); sam.Dispose()
                        | ArrayBinding ta -> ta.Dispose()

                    x.pUniforms |> Array.iter (fun struct (_, ul) -> ul.Dispose())
                    x.pUniformBuffers |> Array.iter (fun struct (_, ub) -> ub.Dispose())
                    x.pStorageBuffers |> Array.iter (fun struct (_, sb) -> sb.Dispose())
                    x.pProgram.Dispose()
                    
                    x.pDepthTestMode.Dispose()
                    x.pCullMode.Dispose()
                    x.pPolygonMode.Dispose()
                    x.pBlendMode.Dispose()
                    x.pStencilMode.Dispose()
                    x.pConservativeRaster.Dispose()
                    x.pMultisample.Dispose()
        )

    interface IDisposable with
        member x.Dispose() = x.Dispose()
 

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PreparedPipelineState =

    type helper() =
        static let nullTexture = Mod.constant (NullTexture() :> ITexture)
        static member NullTexture = nullTexture

    type ResourceManager with 
        member x.CreateUniformBuffers(iface : InterfaceSlots, uniforms : IUniformProvider, scope : Ag.Scope) = 
            iface.uniformBuffers
                |> Array.map (fun (_,block) ->
                    struct (block.ubBinding, x.CreateUniformBuffer(scope, block, uniforms))
                   )
         
        member x.CreateStorageBuffers(iface : InterfaceSlots, uniforms : IUniformProvider, scope : Ag.Scope) = 
            let mutable res = Array.zeroCreate iface.storageBuffers.Length
            let mutable oi = 0

            for (_,buf) in iface.storageBuffers do
                match uniforms.TryGetUniform(scope, Symbol.Create buf.ssbName) with
                | Some (:? IMod<IBuffer> as b) ->
                    let buffer = x.CreateBuffer(b)
                    res.[oi] <- struct (buf.ssbBinding, buffer)
                    oi <- oi + 1
                | Some m ->
                    let o = toBufferCache.Invoke(m)
                    let buffer = x.CreateBuffer(o)
                    res.[oi] <- struct (buf.ssbBinding, buffer)
                    oi <- oi + 1
                | _ ->
                    // missing storage buffer
                    ()

            if oi <> res.Length then Array.Resize(&res, oi)
            res


        member x.CreateTextureBindings(iface : InterfaceSlots, uniforms : IUniformProvider, scope : Ag.Scope) = 

            let samplerModifier = 
                match uniforms.TryGetUniform(scope, DefaultSemantic.SamplerStateModifier) with
                    | Some(:? IMod<Symbol -> SamplerStateDescription -> SamplerStateDescription> as mode) ->
                        Some mode
                    | _ ->
                        None

            let createSammy (sam : FShade.SamplerState) (tex : Symbol) =
            
                let sammy =
                    match samplerModifier with
                        | Some modifier -> 
                            let samplerState = x.GetSamplerStateDescription(sam)
                            x.GetDynamicSamplerState(tex, samplerState, modifier)
                        | None -> 
                            x.GetStaticSamplerState(sam)

                x.CreateSampler(sammy)

            iface.samplers
                |> Array.choose (fun (_,u) ->
                         
                        // check if sampler is an array 
                        //  -> compose (Texture, SamplerState)[] resource
                        //      * case 1: individual slots
                        //      * case 2: dependent on IMod<ITexture[]> and single SamplerState
                        // otherwise
                        //  -> create singe (Texture, SamplerState) resource
                        
                        if u.samplerCount = 0 || u.samplerTextures |> List.isEmpty then 
                            None
                        elif u.samplerCount = 1 then
                                  
                            let (tex, sam) = u.samplerTextures.Head
                            let texSym = Symbol.Create tex

                            let samRes = createSammy sam texSym

                            let texRes = 
                                match uniforms.TryGetUniform(scope, texSym) with
                                | Some tex ->
                                    match tex with
                                    | :? IMod<ITexture> as value -> x.CreateTexture(value)
                                    | :? IMod<IBackendTexture> as value -> x.CreateTexture'(value)
                                    | _ -> x.CreateTexture(helper.NullTexture)
                                | None -> x.CreateTexture(helper.NullTexture)

                            Some struct(Range1i(u.samplerBinding), (SingleBinding (texRes, samRes)))

                        else
                            // FShade allows each slot to be its own unique texture (uniform) and sampler state
                            // check for special cases: 
                            //  1. all the same sampler state
                            //  2. texture uniform names of form TextureUniformName<[0..N-1]>
                            //  && uniform of type ITexture[] is provided
                            
                            let slotRange = Range1i.FromMinAndSize(u.samplerBinding, u.samplerCount - 1)
                            let (tex0, sam0) = u.samplerTextures |> List.head
                            // NOTE: shaderPickler.UnPickle (shader cache) does not preserve reference equal SamplerStates
                            // NOTE2: when using shader modules (switches) the samplerTextures are replaced by the one from the module and thereby will have reference equality
                            let sameSam = u.samplerTextures |> List.skip 1 |> List.forall (fun s -> Object.Equals(sam0, snd s))
                            
                            let arraySingle =
                                if sameSam && tex0.[tex0.Length - 1] = '0' then    
                                    let pre = tex0.Substring(0, tex0.Length - 1)
                                    let mutable arrayNames = true
                                    let mutable i = 1
                                    let mutable tt = u.samplerTextures.Tail // start with second item
                                    while arrayNames && not tt.IsEmpty do
                                        let t = fst tt.Head
                                        arrayNames <- arrayNames && t.StartsWith(pre) && t.Substring(pre.Length) = i.ToString()
                                        tt <- tt.Tail
                                        i <- i + 1

                                    if i = u.samplerCount && arrayNames then
                                        
                                        // try find array uniform
                                        // otherwise try get individual uniforms
                                        let texSym = Symbol.Create pre
                                        match uniforms.TryGetUniform(scope, texSym) with
                                        | Some texArr ->
                                            match texArr with
                                            | :? IMod<ITexture[]> as texArr ->
                                                // create single value (startSlot + count + sam + tex[])

                                                let samRes = createSammy sam0 texSym

                                                let texArr = x.CreateTextureArray(u.samplerCount, texArr)

                                                let arrayBinding = x.CreateTextureBinding'((slotRange, texArr, samRes))

                                                Some arrayBinding
                                            | _ -> 
                                                Log.warn "[GL] invalid texture type %s: %s -> expecting IMod<ITexture[]>" pre (texArr.GetType().Name)
                                                None

                                        | _ -> // could not find texture array uniform -> try individual
                                            None 
                                    else
                                        None // samplerTextures description is not qualified for a single array uniform
                                else
                                    None // samplerTextures description is not qualified for a single array uniform

                            if Option.isSome arraySingle then
                                Some struct(slotRange, ArrayBinding (arraySingle.Value))
                            else 
                                
                                // create array texture binding
                                let textures = u.samplerTextures |> List.mapi (fun i (texName, sam) ->
                                        let texSym = Symbol.Create texName
                                    
                                        let texRes =
                                            match uniforms.TryGetUniform(scope, texSym) with
                                            | Some tex ->
                                                match tex with
                                                | :? IMod<ITexture> as value -> Some (x.CreateTexture(value))
                                                | :? IMod<IBackendTexture> as value -> Some (x.CreateTexture'(value))
                                                | _ -> 
                                                    Log.line "[GL] invalid texture type: %s %s -> expecting IMod<ITexture> or IMod<IBackendTexture>" texName (tex.GetType().Name)
                                                    None
                                            | None -> 
                                                    Log.line "[GL] texture uniform \"%s\" not found" texName
                                                    None

                                        match texRes with
                                        | Some texRes ->

                                            let samRes = createSammy sam texSym

                                            Some (texRes, samRes)
                                        | None -> None
                                    )

                                let binding = x.CreateTextureBinding(slotRange, textures)

                                // fix texture ref-count
                                textures |> List.iter (fun v -> 
                                    match v with
                                    | Some (t, s) -> t.RemoveRef(); s.RemoveRef()
                                    | None -> ())

                                Some struct(slotRange, (ArrayBinding (binding)))
                    )

        member x.CreateColorMasks(fboSignature : IFramebufferSignature, writeBuffers) = 
            let attachments = fboSignature.ColorAttachments |> Map.toList
            let attachmentCount = if attachments.Length > 0 then 1 + (attachments |> List.map (fun (i,_) -> i) |> List.max) else 0

            let colorMasks =
                match writeBuffers with
                | Some b ->
                    let isAll = fboSignature.ColorAttachments |> Map.toSeq |> Seq.forall (fun (_,(sem,_)) -> Set.contains sem b)
                    if isAll then
                        None
                    else
                        let masks = Array.zeroCreate attachmentCount
                        for (index, (sem, att)) in attachments do
                            if Set.contains sem b then
                                masks.[index] <- V4i.IIII
                            else
                                masks.[index] <- V4i.OOOO

                        Some (Array.toList masks)
                | _ ->
                    None

            (colorMasks, attachmentCount)

    let ofRenderObject (fboSignature : IFramebufferSignature) (x : ResourceManager) (rj : RenderObject) =
        // use a context token to avoid making context current/uncurrent repeatedly
        use token = x.Context.ResourceLock
        
        let iface, program = x.CreateSurface(fboSignature, rj.Surface, rj.Mode)
        let slots = x.GetInterfaceSlots(iface)

        GL.Check "[Prepare] Create Surface"
        
        // create all UniformBuffers requested by the program
        let uniformBuffers = x.CreateUniformBuffers(slots, rj.Uniforms, rj.AttributeScope)

        GL.Check "[Prepare] Uniform Buffers"

        let storageBuffers = x.CreateStorageBuffers(slots, rj.Uniforms, rj.AttributeScope)
                
        let textureBindings = x.CreateTextureBindings(slots, rj.Uniforms, rj.AttributeScope)

        GL.Check "[Prepare] Textures"
        
        let (colorMasks, attachmentCount) = x.CreateColorMasks(fboSignature, rj.WriteBuffers)

        let drawBuffers = 
            match rj.WriteBuffers with
                | Some set -> 
                    x.DrawBufferManager.CreateConfig(set) |> Some
                | _ -> None

        let depthMask =
            match rj.WriteBuffers with
                | Some b -> Set.contains DefaultSemantic.Depth b
                | None -> true

        let stencilMask =
            match rj.WriteBuffers with
                | Some b -> Set.contains DefaultSemantic.Stencil b
                | None -> true
                
        let depthTest = x.CreateDepthTest rj.DepthTest
        let depthBias = x.CreateDepthBias rj.DepthBias
        let cullMode = x.CreateCullMode rj.CullMode
        let frontFace = x.CreateFrontFace rj.FrontFace
        let polygonMode = x.CreatePolygonMode rj.FillMode
        let blendMode = x.CreateBlendMode rj.BlendMode
        let stencilMode = x.CreateStencilMode rj.StencilMode
        let conservativeRaster = x.CreateFlag rj.ConservativeRaster
        let multisample = x.CreateFlag rj.Multisample
        
        {
            pUniformProvider = rj.Uniforms
            pContext = x.Context
            pFramebufferSignature = fboSignature
            pProgram = program
            pProgramInterface = iface
            pStorageBuffers = storageBuffers
            pUniformBuffers = uniformBuffers
            pUniforms = Array.empty
            pTextureBindings = textureBindings
            pColorAttachmentCount = attachmentCount
            pDrawBuffers = drawBuffers
            pColorBufferMasks = colorMasks
            pDepthBufferMask = depthMask
            pStencilBufferMask = stencilMask
            pDepthTestMode = depthTest
            pDepthBias = depthBias
            pCullMode = cullMode
            pFrontFace = frontFace
            pPolygonMode = polygonMode
            pBlendMode = blendMode
            pStencilMode = stencilMode
            pConservativeRaster = conservativeRaster
            pMultisample = multisample
        }

    let ofPipelineState (fboSignature : IFramebufferSignature) (x : ResourceManager) (surface : Surface) (rj : PipelineState) =
        // use a context token to avoid making context current/uncurrent repeatedly
        use token = x.Context.ResourceLock
        
        let iface, program = x.CreateSurface(fboSignature, surface, rj.geometryMode)
        let slots = x.GetInterfaceSlots(iface)

        GL.Check "[Prepare] Create Surface"
        
        // create all UniformBuffers requested by the program
        let uniformBuffers = x.CreateUniformBuffers(slots, rj.globalUniforms, Ag.emptyScope)

        GL.Check "[Prepare] Uniform Buffers"

        let storageBuffers = x.CreateStorageBuffers(slots, rj.globalUniforms, Ag.emptyScope)

        let textureBindings = x.CreateTextureBindings(slots, rj.globalUniforms, Ag.emptyScope)

        GL.Check "[Prepare] Textures"
        
        let (colorMasks, attachmentCount) = x.CreateColorMasks(fboSignature, rj.writeBuffers)

        let drawBuffers = 
            match rj.writeBuffers with
                | Some set -> 
                    x.DrawBufferManager.CreateConfig(set) |> Some
                | _ -> None

        let depthMask =
            match rj.writeBuffers with
                | Some b -> Set.contains DefaultSemantic.Depth b
                | None -> true

        let stencilMask =
            match rj.writeBuffers with
                | Some b -> Set.contains DefaultSemantic.Stencil b
                | None -> true
                
        let depthTest = x.CreateDepthTest rj.depthTest
        let depthBias = x.CreateDepthBias rj.depthBias
        let cullMode = x.CreateCullMode rj.cullMode
        let frontFace = x.CreateFrontFace rj.frontFace
        let polygonMode = x.CreatePolygonMode rj.fillMode
        let blendMode = x.CreateBlendMode rj.blendMode
        let stencilMode = x.CreateStencilMode rj.stencilMode
        let conservativeRaster = x.CreateFlag (Mod.constant false)
        let multisample = x.CreateFlag rj.multisample
        
        
        {
            pUniformProvider = rj.globalUniforms
            pContext = x.Context
            pFramebufferSignature = fboSignature
            pProgram = program
            pProgramInterface = iface
            pStorageBuffers = storageBuffers
            pUniformBuffers = uniformBuffers
            pUniforms = Array.empty
            //pTextureSlots = textureSlots
            pTextureBindings = textureBindings
            pColorAttachmentCount = attachmentCount
            pDrawBuffers = drawBuffers
            pColorBufferMasks = colorMasks
            pDepthBufferMask = depthMask
            pStencilBufferMask = stencilMask
            pDepthTestMode = depthTest
            pDepthBias = depthBias
            pCullMode = cullMode
            pFrontFace = frontFace
            pPolygonMode = polygonMode
            pBlendMode = blendMode
            pStencilMode = stencilMode
            pConservativeRaster = conservativeRaster
            pMultisample = multisample
        }
  

type NativeStats =
    struct
        val mutable public InstructionCount : int
        static member Zero = NativeStats()
        static member (+) (l : NativeStats, r : NativeStats) = NativeStats(InstructionCount = l.InstructionCount + r.InstructionCount)
        static member (-) (l : NativeStats, r : NativeStats) = NativeStats(InstructionCount = l.InstructionCount - r.InstructionCount)
    end


[<AutoOpen>]
module PreparedPipelineStateAssembler =
    
    let private usedClipPlanes (iface : GLSLProgramInterface) =
        let candidates = [FShade.ShaderStage.Geometry; FShade.ShaderStage.TessEval; FShade.ShaderStage.Vertex ]
        let beforeRasterize = candidates |> List.tryPick (fun s -> MapExt.tryFind s iface.shaders)
        match beforeRasterize with
        | Some shader ->
            match MapExt.tryFind "gl_ClipDistance" shader.shaderBuiltInOutputs with
            | Some t ->
                let cnt = 
                    match t with
                    | GLSLType.Array(len,_,_) -> len
                    | _ -> 8
                Seq.init cnt id |> Set.ofSeq
            | None ->
                Set.empty
        | None ->
            Set.empty

    type ICommandStream with
    
        member x.SetPipelineState(s : CompilerInfo, me : PreparedPipelineState) : NativeStats =

            let mutable icnt = 0 // counting dynamic instructions

            x.SetDepthMask(me.pDepthBufferMask)
            x.SetStencilMask(me.pStencilBufferMask)
            match me.pDrawBuffers with
                | None ->
                    x.SetDrawBuffers(s.drawBufferCount, s.drawBuffers)
                | Some b ->
                    x.SetDrawBuffers(b.Count, NativePtr.toNativeInt b.Buffers)
                                       
            x.SetDepthTest(me.pDepthTestMode)  
            x.SetDepthBias(me.pDepthBias)
            x.SetPolygonMode(me.pPolygonMode)
            x.SetCullMode(me.pCullMode)
            x.SetFrontFace(me.pFrontFace)
            x.SetBlendMode(me.pBlendMode)
            x.SetStencilMode(me.pStencilMode)
            x.SetMultisample(me.pMultisample)
            
            let myProg = me.pProgram.Handle.GetValue()
            x.UseProgram(me.pProgram)
            if myProg.WritesPointSize then
                x.Enable(int OpenTK.Graphics.OpenGL4.EnableCap.ProgramPointSize)
            else
                x.Disable(int OpenTK.Graphics.OpenGL4.EnableCap.ProgramPointSize)

            let meUsed = usedClipPlanes me.pProgramInterface
            for i in meUsed do
                x.Enable(int OpenTK.Graphics.OpenGL4.EnableCap.ClipDistance0 + i)
                icnt <- icnt + 1
            

            // bind all uniform-buffers (if needed)
            for struct (id, ub) in me.pUniformBuffers do
                x.BindUniformBufferView(id, ub)
                icnt <- icnt + 1

            for struct (id, ssb) in me.pStorageBuffers do
                x.BindStorageBuffer(id, ssb)
                icnt <- icnt + 1

            // bind all textures/samplers (if needed)
            for struct (slotRange, binding) in me.pTextureBindings do
                match binding with 
                | SingleBinding (tex, sam) ->
                    x.SetActiveTexture(slotRange.Min)
                    x.BindTexture(tex)
                    x.BindSampler(slotRange.Min, sam)
                    icnt <- icnt + 3
                | ArrayBinding ta ->
                    x.BindTexturesAndSamplers(ta) // internally will use 2 OpenGL calls glBindTextures and glBindSamplers
                    icnt <- icnt + 1 
                    
            // bind all top-level uniforms (if needed)
            for struct (id, u) in me.pUniforms do
                x.BindUniformLocation(id, u)
                icnt <- icnt + 1

            NativeStats(InstructionCount = icnt + 14) // 14 fixed instruction 
            

        member x.SetPipelineState(s : CompilerInfo, me : PreparedPipelineState, prev : PreparedPipelineState) : NativeStats =
            
            let mutable icnt = 0

            if prev.pDepthBufferMask <> me.pDepthBufferMask then
                x.SetDepthMask(me.pDepthBufferMask)
                icnt <- icnt + 1

            if prev.pStencilBufferMask <> me.pStencilBufferMask then
                x.SetStencilMask(me.pStencilBufferMask)
                icnt <- icnt + 1

            if prev.pDrawBuffers <> me.pDrawBuffers then
                match me.pDrawBuffers with
                    | None ->
                        x.SetDrawBuffers(s.drawBufferCount, s.drawBuffers)
                    | Some b ->
                        x.SetDrawBuffers(b.Count, NativePtr.toNativeInt b.Buffers)
                icnt <- icnt + 1
                   
            if prev.pDepthTestMode <> me.pDepthTestMode then
                x.SetDepthTest(me.pDepthTestMode)  
                icnt <- icnt + 1

            if prev.pDepthBias <> me.pDepthBias then
                x.SetDepthBias(me.pDepthBias)  
                icnt <- icnt + 1
                
            if prev.pPolygonMode <> me.pPolygonMode then
                x.SetPolygonMode(me.pPolygonMode)
                icnt <- icnt + 1
                
            if prev.pCullMode <> me.pCullMode then
                x.SetCullMode(me.pCullMode)
                icnt <- icnt + 1
            
            if prev.pFrontFace <> me.pFrontFace then
                x.SetFrontFace(me.pFrontFace)
                icnt <- icnt + 1

            if prev.pBlendMode <> me.pBlendMode then
                x.SetBlendMode(me.pBlendMode)
                icnt <- icnt + 1

            if prev.pStencilMode <> me.pStencilMode then
                x.SetStencilMode(me.pStencilMode)
                icnt <- icnt + 1

            if prev.pMultisample <> me.pMultisample then
                x.SetMultisample(me.pMultisample)
                icnt <- icnt + 1

            if prev.pProgram <> me.pProgram then
                let myProg = me.pProgram.Handle.GetValue()
                x.UseProgram(me.pProgram)
                icnt <- icnt + 1

                if obj.ReferenceEquals(prev.pProgram, null) || prev.pProgram.Handle.GetValue().WritesPointSize <> myProg.WritesPointSize then
                    if myProg.WritesPointSize then
                        x.Enable(int OpenTK.Graphics.OpenGL4.EnableCap.ProgramPointSize)
                    else
                        x.Disable(int OpenTK.Graphics.OpenGL4.EnableCap.ProgramPointSize)
                    icnt <- icnt + 1
            

            let prevUsed = usedClipPlanes prev.pProgramInterface
            let meUsed = usedClipPlanes me.pProgramInterface

            if prevUsed <> meUsed then
                for i in meUsed do
                    x.Enable(int OpenTK.Graphics.OpenGL4.EnableCap.ClipDistance0 + i)
                    icnt <- icnt + 1


            // bind all uniform-buffers (if needed)
            let mutable j = 0
            for struct (id, ub) in me.pUniformBuffers do
                let mutable i = j
                let mutable old = None
                // compare with prev state in parallel / assuming bindings are sorted
                while i < prev.pUniformBuffers.Length do
                    let struct (slt, bnd) = prev.pUniformBuffers.[i]
                    if slt = id then old <- Some bnd
                    if slt < id then i <- i + 1; j <- j + 1
                    else i <- 9999 // break

                match old with
                | Some old when old = ub -> 
                    () // the same UniformBuffer has already been bound
                | _ -> 
                    x.BindUniformBufferView(id, ub)
                    icnt <- icnt + 1

            let mutable j = 0
            for struct (id, ssb) in me.pStorageBuffers do
                let mutable i = j
                let mutable old = None
                // compare with prev state in parallel / assuming bindings are sorted
                while i < prev.pStorageBuffers.Length do
                    let struct (slt, bnd) = prev.pStorageBuffers.[i]
                    if slt = id then old <- Some bnd
                    if slt < id then i <- i + 1; j <- j + 1
                    else i <- 9999 // break

                match old with
                | Some old when old = ssb -> 
                    // the same UniformBuffer has already been bound
                    ()
                | _ -> 
                    x.BindStorageBuffer(id, ssb)
                icnt <- icnt + 1

            // bind all textures/samplers (if needed)
            let mutable j = 0
            for struct (slotRange, binding) in me.pTextureBindings do
                let mutable i = j
                let mutable old = None
                // compare with prev state in parallel / assuming bindings are sorted
                while i < prev.pTextureBindings.Length do
                    let struct (slt, bnd) = prev.pTextureBindings.[i]
                    if slt = slotRange then old <- Some bnd // ranges must perfectly match / does not support overlap of arrays or single textures
                    if slt.Max < slotRange.Min then i <- i + 1; j <- j + 1
                    else i <- Int32.MaxValue // break
                    
                match old with
                | Some old when old = binding -> () // could use more sophisticated compare to detect array overlaps 
                | _ -> 
                    match binding with 
                    | SingleBinding (tex, sam) ->
                        x.SetActiveTexture(slotRange.Min)
                        x.BindTexture(tex)
                        match old with
                        | Some old ->
                            match old with 
                            | SingleBinding (otex, osam) when Object.ReferenceEquals(osam, sam) -> ()
                            | _ ->
                                x.BindSampler(slotRange.Min, sam); icnt <- icnt + 1
                        | None -> x.BindSampler(slotRange.Min, sam); icnt <- icnt + 1
                        icnt <- icnt + 2
                    | ArrayBinding ta ->
                        x.BindTexturesAndSamplers(ta) // internally will use 2 OpenGL calls glBindTextures and glBindSamplers
                        icnt <- icnt + 1 

            // bind all top-level uniforms (if needed)
            let mutable j = 0
            for struct (id, u) in me.pUniforms do
                let mutable i = j
                let mutable old = None
                // compare with prev state in parallel / assuming bindings are sorted
                while i < prev.pUniforms.Length do
                    let struct (slt, bnd) = prev.pUniforms.[i]
                    if slt = id then old <- Some bnd
                    if slt < id then i <- i + 1; j <- j + 1
                    else i <- Int32.MaxValue // break

                match old with
                    | Some old when old = u -> ()
                    | _ -> x.BindUniformLocation(id, u); icnt <- icnt + 1
                

            NativeStats(InstructionCount = icnt)

        member x.SetPipelineState(s : CompilerInfo, me : PreparedPipelineState, prev : Option<PreparedPipelineState>) : NativeStats =
            match prev with
                | Some prev -> x.SetPipelineState(s, me, prev)
                | None -> x.SetPipelineState(s, me)


[<AbstractClass>]
type PreparedCommand(ctx : Context, renderPass : RenderPass) =
    
    let mutable refCount = 1
    let id = newId()

    let mutable cleanup : list<unit -> unit> = []
    
    let mutable resourceStats = None
    let mutable resources = None
    
    let getResources (x : PreparedCommand) =
        lock x (fun () ->
            match resources with
            | Some res -> res
            | _ -> 
                let all = x.GetResources() |> Seq.toArray
                resources <- Some all
                all
        )

    let getStats (x : PreparedCommand) =
        lock x (fun () ->
            match resourceStats with
            | Some s -> s
            | _ ->
                let res = getResources x
                let cnt = res.Length
                let counts = res |> Seq.countBy (fun r -> r.Kind) |> Map.ofSeq
                resourceStats <- Some (cnt, counts)
                (cnt, counts)
        )
        
    abstract member GetResources : unit -> seq<IResource>
    abstract member Release : unit -> unit
    abstract member Compile : info : CompilerInfo * stream : ICommandStream * prev : Option<PreparedCommand> -> NativeStats
    abstract member EntryState : Option<PreparedPipelineState>
    abstract member ExitState : Option<PreparedPipelineState>
    
    member x.AddCleanup(clean : unit -> unit) =
        cleanup <- clean :: cleanup

    member x.Id = id
    member x.Pass = renderPass
    member x.IsDisposed = refCount = 0
    member x.Context = ctx

    member x.Resources = getResources x
        
    member x.ResourceCount =
        let (cnt,_) = getStats x
        cnt

    member x.ResourceCounts =
        let (_,cnts) = getStats x
        cnts

    member x.AddReference() =
        Interlocked.Increment(&refCount) |> ignore

    member x.Update(token : AdaptiveToken, rt : RenderToken) =
        for r in x.Resources do r.Update(token, rt)

    member x.Dispose() =
        if Interlocked.Decrement(&refCount) = 0 then
            lock x (fun () ->
                let token = try Some ctx.ResourceLock with :? ObjectDisposedException -> None
            
                match token with
                | Some token ->
                    try
                        cleanup |> List.iter (fun f -> f())
                        x.Release()
                        cleanup <- []
                        resourceStats <- None
                        resources <- None
                    finally
                        token.Dispose()
                | None ->
                    //OpenGL died
                    ()
            )

    interface IRenderObject with
        member x.AttributeScope = Ag.emptyScope
        member x.Id = x.Id
        member x.RenderPass = renderPass

    interface IPreparedRenderObject with
        member x.Update(t,rt) = x.Update(t, rt)
        member x.Original = None
        member x.Dispose() = x.Dispose()

type PreparedObjectInfo =
    {   
        oContext : Context
        oActivation : IDisposable
        oFramebufferSignature : IFramebufferSignature
        oBeginMode : IResource<GLBeginMode, GLBeginMode>
        oBuffers : list<int * BufferView * AttributeFrequency * IResource<Buffer, int>>
        oIndexBuffer : Option<OpenGl.Enums.IndexType * IResource<Buffer, int>>
        oIsActive : IResource<bool, int>
        oDrawCallInfos : IResource<DrawCallInfoList, DrawCallInfoList>
        oIndirectBuffer : Option<IResource<IndirectBuffer, V2i>>
        oVertexInputBinding : IResource<VertexInputBindingHandle, int>  
    }
    
    member x.Dispose() =
        x.oBeginMode.Dispose()

        for (_,_,_,b) in x.oBuffers do b.Dispose()
        match x.oIndexBuffer with
            | Some (_,b) -> b.Dispose()
            | _ -> ()

        x.oIsActive.Dispose()
        match x.oIndirectBuffer with
            | Some i -> i.Dispose()
            | None -> x.oDrawCallInfos.Dispose()

        x.oVertexInputBinding.Dispose()

    member x.Resources =
        seq {
            yield x.oBeginMode :> IResource

            for (_,_,_,b) in x.oBuffers do yield b :>_
            match x.oIndexBuffer with
                | Some (_,b) -> yield b :>_
                | _ -> ()

            yield x.oIsActive :> _
            match x.oIndirectBuffer with
                | Some i -> yield i :> _
                | None -> yield x.oDrawCallInfos :> _

            yield x.oVertexInputBinding :> _
        }

module PreparedObjectInfo =
    open FShade.GLSL

    let private (|Floaty|_|) (t : GLSLType) =
        match t with
            | GLSLType.Float 32 -> Some ()
            | GLSLType.Float 64 -> Some ()
            | _ -> None
        

    let private getExpectedType (t : GLSLType) =
        match t with
            | GLSLType.Void -> typeof<unit>
            | GLSLType.Bool -> typeof<int>
            | Floaty -> typeof<float32>
            | GLSLType.Int(true, 32) -> typeof<int>
            | GLSLType.Int(false, 32) -> typeof<uint32>
             
            | GLSLType.Vec(2, Floaty) -> typeof<V2f>
            | GLSLType.Vec(3, Floaty) -> typeof<V3f>
            | GLSLType.Vec(4, Floaty) -> typeof<V4f>
            | GLSLType.Vec(2, Int(true, 32)) -> typeof<V2i>
            | GLSLType.Vec(3, Int(true, 32)) -> typeof<V3i>
            | GLSLType.Vec(4, Int(true, 32)) -> typeof<V4i>
            | GLSLType.Vec(3, Int(false, 32)) -> typeof<C3ui>
            | GLSLType.Vec(4, Int(false, 32)) -> typeof<C4ui>


            | GLSLType.Mat(2,2,Floaty) -> typeof<M22f>
            | GLSLType.Mat(3,3,Floaty) -> typeof<M33f>
            | GLSLType.Mat(4,4,Floaty) -> typeof<M44f>
            | GLSLType.Mat(3,4,Floaty) -> typeof<M34f>
            | GLSLType.Mat(4,3,Floaty) -> typeof<M34f>
            | GLSLType.Mat(2,3,Floaty) -> typeof<M23f>

            | _ -> failwithf "[GL] unexpected vertex type: %A" t


    let ofRenderObject (fboSignature : IFramebufferSignature) (x : ResourceManager) (iface : GLSLProgramInterface) (program : IResource<Program, int>) (rj : RenderObject) =

        // use a context token to avoid making context current/uncurrent repeatedly
        use token = x.Context.ResourceLock

        let activation = rj.Activate()

        // create all requested vertex-/instance-inputs
        let buffers =
            iface.inputs
                |> List.choose (fun v ->
                     if v.paramLocation >= 0 then
                        let expected = getExpectedType v.paramType
                        let sem = v.paramName |> Symbol.Create
                        match rj.VertexAttributes.TryGetAttribute sem with
                            | Some value ->
                                let dep = x.CreateBuffer(value.Buffer)
                                Some (v.paramLocation, value, AttributeFrequency.PerVertex, dep)
                            | _  -> 
                                match rj.InstanceAttributes with
                                    | null -> failwithf "could not get attribute %A (not found in vertex attributes, and instance attributes is null) for rj: %A" sem rj
                                    | _ -> 
                                        match rj.InstanceAttributes.TryGetAttribute sem with
                                            | Some value ->
                                                let dep = x.CreateBuffer(value.Buffer)
                                                Some(v.paramLocation, value, (AttributeFrequency.PerInstances 1), dep)
                                            | _ -> 
                                                failwithf "could not get attribute %A" sem
                        else
                            None
                   )

        GL.Check "[Prepare] Buffers"

        // create the index buffer (if present)
        let index =
            match rj.Indices with
                | Some i -> 
                    let buffer = x.CreateBuffer i.Buffer
                    let indexType =
                        let indexType = i.ElementType
                        if indexType = typeof<byte> then OpenGl.Enums.IndexType.UnsignedByte
                        elif indexType = typeof<uint16> then OpenGl.Enums.IndexType.UnsignedShort
                        elif indexType = typeof<uint32> then OpenGl.Enums.IndexType.UnsignedInt
                        elif indexType = typeof<sbyte> then OpenGl.Enums.IndexType.UnsignedByte
                        elif indexType = typeof<int16> then OpenGl.Enums.IndexType.UnsignedShort
                        elif indexType = typeof<int32> then OpenGl.Enums.IndexType.UnsignedInt
                        else failwithf "unsupported index type: %A"  indexType
                    Some(indexType, buffer)

                | None -> None


        GL.Check "[Prepare] Indices"

        let indirect =
            if isNull rj.IndirectBuffer then None
            else x.CreateIndirectBuffer(Option.isSome rj.Indices, rj.IndirectBuffer) |> Some

        GL.Check "[Prepare] Indirect Buffer"

        // create the VertexArrayObject
        let vibh =
            x.CreateVertexInputBinding(buffers, index)

        GL.Check "[Prepare] VAO"

        let attachments = fboSignature.ColorAttachments |> Map.toList
        let attachmentCount = if attachments.Length > 0 then 1 + (attachments |> List.map (fun (i,_) -> i) |> List.max) else 0

        let isActive = x.CreateIsActive rj.IsActive
        let beginMode = x.CreateBeginMode(program.Handle, rj.Mode)
        let drawCalls = if isNull rj.DrawCallInfos then Unchecked.defaultof<_> else x.CreateDrawCallInfoList rj.DrawCallInfos


        // finally return the PreparedRenderObject
        
        {
            oContext = x.Context
            oActivation = activation
            oFramebufferSignature = fboSignature
            oBuffers = buffers
            oIndexBuffer = index
            oIndirectBuffer = indirect
            oVertexInputBinding = vibh
            oIsActive = isActive
            oBeginMode = beginMode
            oDrawCallInfos = drawCalls
        }
            

[<AutoOpen>]
module PreparedObjectInfoAssembler =
    
    type ICommandStream with
        member x.Render(s : CompilerInfo, me : PreparedObjectInfo) : NativeStats =
        
            // bind the VAO (if needed)
            x.BindVertexAttributes(s.contextHandle, me.oVertexInputBinding)

            // draw the thing
            let isActive = me.oIsActive
            let beginMode = me.oBeginMode

            match me.oIndirectBuffer with
                | Some indirect ->
                    match me.oIndexBuffer with
                        | Some (it,_) ->
                            x.DrawElementsIndirect(s.runtimeStats, isActive, beginMode, int it, indirect)
                        | None ->
                            x.DrawArraysIndirect(s.runtimeStats, isActive, beginMode, indirect)

                | None ->
                    match me.oIndexBuffer with
                        | Some (it,_) ->
                            x.DrawElements(s.runtimeStats, isActive, beginMode, (int it), me.oDrawCallInfos)
                        | None ->
                            x.DrawArrays(s.runtimeStats, isActive, beginMode, me.oDrawCallInfos)

            NativeStats(InstructionCount = 2)

        member x.Render(s : CompilerInfo, me : PreparedObjectInfo, prev : PreparedObjectInfo) : NativeStats =
        
            let mutable icnt = 0

            // bind the VAO (if needed)
            if prev.oVertexInputBinding <> me.oVertexInputBinding then
                x.BindVertexAttributes(s.contextHandle, me.oVertexInputBinding)
                icnt <- icnt + 1

            // draw the thing
            let isActive = me.oIsActive
            let beginMode = me.oBeginMode

            match me.oIndirectBuffer with
                | Some indirect ->
                    match me.oIndexBuffer with
                        | Some (it,_) ->
                            x.DrawElementsIndirect(s.runtimeStats, isActive, beginMode, int it, indirect)
                        | None ->
                            x.DrawArraysIndirect(s.runtimeStats, isActive, beginMode, indirect)

                | None ->
                    match me.oIndexBuffer with
                        | Some (it,_) ->
                            x.DrawElements(s.runtimeStats, isActive, beginMode, (int it), me.oDrawCallInfos)
                        | None ->
                            x.DrawArrays(s.runtimeStats, isActive, beginMode, me.oDrawCallInfos)

            NativeStats(InstructionCount = icnt + 1)

        member x.Render(s : CompilerInfo, me : PreparedObjectInfo, prev : Option<PreparedObjectInfo>) : NativeStats =
            match prev with
                | Some prev -> x.Render(s, me, prev)
                | None -> x.Render(s, me)
    
            
type EpilogCommand(ctx : Context) =
    inherit PreparedCommand(ctx, RenderPass.main) 

    override x.GetResources() = Seq.empty
    override x.Release() = ()
    override x.Compile(s, stream, prev) = 
        stream.SetDepthMask(true)
        stream.SetStencilMask(true)
        stream.SetDrawBuffers(s.drawBufferCount, s.drawBuffers)
        stream.UseProgram(0)
        stream.BindBuffer(int OpenTK.Graphics.OpenGL4.BufferTarget.DrawIndirectBuffer, 0)
        for i in 0 .. 7 do
            stream.Disable(int OpenTK.Graphics.OpenGL4.EnableCap.ClipDistance0 + i)
        NativeStats(InstructionCount = 13)

    override x.EntryState = None
    override x.ExitState = None

type NopCommand(ctx : Context, pass : RenderPass) =
    inherit PreparedCommand(ctx, pass) 

    override x.GetResources() = Seq.empty
    override x.Release() = ()
    override x.Compile(_,_,_) = NativeStats.Zero
    override x.EntryState = None
    override x.ExitState = None

type PreparedObjectCommand(state : PreparedPipelineState, info : PreparedObjectInfo, renderPass : RenderPass) =
    inherit PreparedCommand(state.pContext, renderPass)

    member x.Info = info

    override x.Release() =
        state.Dispose()
        info.Dispose()

    override x.GetResources() =
        seq {
            yield! state.Resources
            yield! info.Resources
        }

    override x.Compile(s : CompilerInfo, stream : ICommandStream, prev : Option<PreparedCommand>) : NativeStats =
        let prevInfo =
            match prev with
                | Some (:? PreparedObjectCommand as p) -> Some p.Info
                | _ -> None

        let prevState =
            match prev with
            | Some p -> p.ExitState
            | _ -> None

            
        let stats = stream.SetPipelineState(s, state, prevState)
        stats + stream.Render(s, info, prevInfo)

    override x.EntryState = Some state
    override x.ExitState = Some state

type MultiCommand(ctx : Context, cmds : list<PreparedCommand>, renderPass : RenderPass) =
    inherit PreparedCommand(ctx, renderPass)
    
    let first   = List.tryHead cmds
    let last    = List.tryLast cmds

    override x.Release() =
        cmds |> List.iter (fun c -> c.Dispose())
        
    override x.GetResources() =
        cmds |> Seq.collect (fun c -> c.Resources)

    override x.Compile(info, stream, prev) =
        let mutable prev = prev
        let mutable s = NativeStats.Zero
        for c in cmds do
            s <- s + c.Compile(info, stream, prev)
            prev <- Some c
        s

    override x.EntryState = first |> Option.bind (fun first -> first.EntryState)
    override x.ExitState = last |> Option.bind (fun last -> last.ExitState)


module PreparedCommand =

    let ofRenderObject (fboSignature : IFramebufferSignature) (x : ResourceManager) (o : IRenderObject) =

        let rec ofRenderObject (owned : bool) (fboSignature : IFramebufferSignature) (x : ResourceManager) (o : IRenderObject) =
            let pass = o.RenderPass
            match o with
                | :? RenderObject as o ->
                    let state = PreparedPipelineState.ofRenderObject fboSignature x o
                    let info = PreparedObjectInfo.ofRenderObject fboSignature x state.pProgramInterface state.pProgram o
                    new PreparedObjectCommand(state, info, pass) :> PreparedCommand

                | :? MultiRenderObject as o ->
                    match o.Children with
                        | [] -> 
                            new NopCommand(x.Context, pass) :> PreparedCommand
                        | [o] -> 
                            ofRenderObject owned fboSignature x o

                        | l -> 
                            new MultiCommand(x.Context, l |> List.map (ofRenderObject owned fboSignature x), pass) :> PreparedCommand

                | :? PreparedCommand as cmd ->
                    if not owned then cmd.AddReference()
                    cmd

                | :? ICustomRenderObject as o ->
                    o.Create(fboSignature.Runtime, fboSignature) |> ofRenderObject true fboSignature x

                | _ ->
                    failwithf "bad object: %A" o

        ofRenderObject false fboSignature x o