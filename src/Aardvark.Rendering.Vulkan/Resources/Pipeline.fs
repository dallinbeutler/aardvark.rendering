﻿namespace Aardvark.Rendering.Vulkan

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Rendering.Vulkan
open Microsoft.FSharp.NativeInterop

#nowarn "9"
// #nowarn "51"

type PipelineDescription =
    {
        renderPass              : RenderPass
        shaderProgram           : ShaderProgram
        vertexInputState        : Map<Symbol, VertexInputDescription>
        inputAssembly           : InputAssemblyState
        rasterizerState         : RasterizerState
        colorBlendState         : ColorBlendState
        multisampleState        : MultisampleState
        depthState              : DepthState
        stencilState            : StencilState
        dynamicStates           : VkDynamicState[]
    }


type Pipeline =
    class
        inherit Resource<VkPipeline>
        val mutable public Description : PipelineDescription

        new(device : Device, handle : VkPipeline, description : PipelineDescription) = { inherit Resource<_>(device, handle); Description = description }
    end


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Pipeline =

    module private List =
        let collecti (f : int -> 'a -> list<'b>) (m : list<'a>) =
            m |> List.indexed |> List.collect (fun (i,v) -> f i v)

    let createGraphics (desc : PipelineDescription) (device : Device) =
        let vkbool b = if b then 1u else 0u

        let prog = desc.shaderProgram

        let inputs = prog.Inputs |> List.sortBy (fun p -> p.paramLocation)

        let paramsWithInputs =
            inputs |> List.map (fun p ->
                match Map.tryFind (Symbol.Create p.paramSemantic) desc.vertexInputState with
                    | Some ip -> 
                        p.paramLocation, p, ip
                    | None ->
                        failf "could not get vertex input-type for %A" p
            )

        let inputBindings =
            paramsWithInputs |> List.mapi (fun i (loc, p, ip) ->
                VkVertexInputBindingDescription(
                    uint32 i,
                    uint32 ip.stride,
                    ip.stepRate
                )
            )

        let inputAttributes =
            paramsWithInputs |> List.collecti (fun bi (loc, p, ip) ->
                ip.offsets |> List.mapi (fun i off ->
                    VkVertexInputAttributeDescription(
                        uint32 (loc + i),
                        uint32 bi,
                        ip.inputFormat,
                        uint32 off
                    )
                )
            )

        native {

            let! pInputBindings = inputBindings
            let! pInputAttributes = inputAttributes

            let! pVertexInputState =
                VkPipelineVertexInputStateCreateInfo(
                    VkStructureType.PipelineVertexInputStateCreateInfo, 0n, 
                    VkPipelineVertexInputStateCreateFlags.MinValue,

                    uint32 inputBindings.Length,
                    pInputBindings,

                    uint32 inputAttributes.Length,
                    pInputAttributes
                )

            let! pInputAssemblyState =
                VkPipelineInputAssemblyStateCreateInfo(
                    VkStructureType.PipelineInputAssemblyStateCreateInfo, 0n, 
                    VkPipelineInputAssemblyStateCreateFlags.MinValue,

                    desc.inputAssembly.topology,
                    vkbool desc.inputAssembly.restartEnable
                )
        
            let! pRasterizerState =
                let rs = desc.rasterizerState
                VkPipelineRasterizationStateCreateInfo(
                    VkStructureType.PipelineRasterizationStateCreateInfo, 0n,
                    VkPipelineRasterizationStateCreateFlags.MinValue,
                
                    vkbool rs.depthClampEnable,
                    0u, //vkbool rs.rasterizerDiscardEnable, //breaks if true
                    rs.polygonMode,
                    rs.cullMode,
                    rs.frontFace,
                    vkbool rs.depthBiasEnable,
                    float32 rs.depthBiasConstantFactor,
                    float32 rs.depthBiasClamp,
                    float32 rs.depthBiasSlopeFactor,
                    float32 rs.lineWidth
                )


            let! pAttachmentBlendStates = 
                desc.colorBlendState.attachmentStates |> Array.map (fun s ->
                    VkPipelineColorBlendAttachmentState(
                        vkbool s.enabled,
                        s.srcFactor, s.dstFactor, s.operation,
                        s.srcFactorAlpha, s.dstFactorAlpha, s.operationAlpha,
                        s.colorWriteMask
                    )
                )

            let! pColorBlendState =
                let cb = desc.colorBlendState
                VkPipelineColorBlendStateCreateInfo(
                    VkStructureType.PipelineColorBlendStateCreateInfo, 0n,
                    VkPipelineColorBlendStateCreateFlags.MinValue,

                    vkbool cb.logicOpEnable,
                    cb.logicOp,
                    uint32 cb.attachmentStates.Length,
                    pAttachmentBlendStates,
                    cb.constants
                )


            let! pViewportState =
            
                let vp  =
                    if device.AllCount > 1u then
                        if desc.renderPass.LayerCount > 1 then 1u
                        else device.AllCount
                    else 1u

                VkPipelineViewportStateCreateInfo(
                    VkStructureType.PipelineViewportStateCreateInfo, 0n,
                    VkPipelineViewportStateCreateFlags.MinValue,
                
                    uint32 vp,
                    NativePtr.zero,

                    uint32 vp,
                    NativePtr.zero
                )

            let! pSampleMasks = desc.multisampleState.sampleMask
            let! pMultisampleState =
                let ms = desc.multisampleState
                VkPipelineMultisampleStateCreateInfo(
                    VkStructureType.PipelineMultisampleStateCreateInfo, 0n,
                    VkPipelineMultisampleStateCreateFlags.MinValue,
                
                    unbox ms.samples,
                    vkbool ms.sampleShadingEnable,
                    float32 ms.minSampleShading,
                    pSampleMasks,
                    vkbool ms.alphaToCoverageEnable,
                    vkbool ms.alphaToOneEnable
                )


            let! pDepthStencilState =
                let d = desc.depthState
                let s = desc.stencilState
                VkPipelineDepthStencilStateCreateInfo(
                    VkStructureType.PipelineDepthStencilStateCreateInfo, 0n,
                    VkPipelineDepthStencilStateCreateFlags.MinValue,
                
                    vkbool d.testEnabled,
                    vkbool d.writeEnabled,
                    d.compare,
                    vkbool d.boundsTest,
                    vkbool s.enabled,
                    s.front,
                    s.back,
                    float32 d.depthBounds.Min,
                    float32 d.depthBounds.Max
                )

            let shaderCreateInfos = desc.shaderProgram.ShaderCreateInfos
            let! pShaderCreateInfos = shaderCreateInfos

            let! pDynamicStates = Array.map uint32 desc.dynamicStates

            let! pDynamicStates =
                VkPipelineDynamicStateCreateInfo(
                    VkStructureType.PipelineDynamicStateCreateInfo, 0n,
                    VkPipelineDynamicStateCreateFlags.MinValue, 

                    uint32 desc.dynamicStates.Length,
                    NativePtr.cast pDynamicStates
                )

            let! pTess =
                VkPipelineTessellationStateCreateInfo(
                    VkStructureType.PipelineTessellationStateCreateInfo, 0n,
                    VkPipelineTessellationStateCreateFlags.MinValue,
                    10u
                )

            let! pPipelineCreateInfo =
                VkGraphicsPipelineCreateInfo(
                    VkStructureType.GraphicsPipelineCreateInfo,
                    0n, VkPipelineCreateFlags.None,
                    uint32 shaderCreateInfos.Length,
                    pShaderCreateInfos,
                    pVertexInputState,
                    pInputAssemblyState,
                    NativePtr.zero,
                    pViewportState,
                    pRasterizerState,
                    pMultisampleState,
                    pDepthStencilState,
                    pColorBlendState,
                    pDynamicStates,
                    desc.shaderProgram.PipelineLayout.Handle,
                    desc.renderPass.Handle,
                    0u,
                    VkPipeline.Null,
                    -1
                )

            let! pPipeline = VkPipeline.Null
            VkRaw.vkCreateGraphicsPipelines(device.Handle, VkPipelineCache.Null, 1u, pPipelineCreateInfo, NativePtr.zero, pPipeline) 
                |> check "vkCreateGraphicsPipelines"
                
            return Pipeline(device, !!pPipeline, desc)
        }
    let delete (p : Pipeline) (device : Device) =
        if p.Handle.IsValid then
            VkRaw.vkDestroyPipeline(device.Handle, p.Handle, NativePtr.zero)
            p.Handle <- VkPipeline.Null


[<AbstractClass; Sealed; Extension>]
type ContextPipelineExtensions private() =
    [<Extension>]
    static member inline CreateGraphicsPipeline(this : Device, description : PipelineDescription) =
        this |> Pipeline.createGraphics description

    [<Extension>]
    static member inline Delete(this : Device, pipeline : Pipeline) =
        this |> Pipeline.delete pipeline
