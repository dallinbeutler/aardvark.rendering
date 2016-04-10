﻿namespace Aardvark.Rendering.Vulkan

#nowarn "9"
#nowarn "51"

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Collections.Concurrent
open Microsoft.FSharp.NativeInterop
open Aardvark.Base

type Queue(device : Device, cmdPool : ThreadLocal<CommandPool>, family : PhysicalQueueFamily, handle : VkQueue, index : int) =
    member x.CommandPool = cmdPool.Value
    member x.Device = device
    member x.Family = family
    member x.Handle = handle
    member x.QueueIndex = index

type QueuePool(family : PhysicalQueueFamily, queues : Queue[]) =
    let availableCount = new SemaphoreSlim(queues.Length)
    let available = ConcurrentQueue<Queue> queues

    member x.Family = family

    member x.Acquire() =
        availableCount.Wait()
        match available.TryDequeue() with
            | (true, q) -> q
            | _ -> failf "could not acquire a Queue"

    member x.AcquireAsync() =
        async {
            let! _ = Async.AwaitIAsyncResult (availableCount.WaitAsync())
            match available.TryDequeue() with
                | (true, q) -> return q
                | _ -> return failf "could not acquire a Queue"
        }

    member x.Release(q : Queue) =
        available.Enqueue(q)
        availableCount.Release() |> ignore


[<AbstractClass; Sealed; Extension>]
type QueueCommandExtensions private() =

    static let submit(this : Queue, cmd : CommandBuffer[]) =
        let start() =
            let ptrs = NativePtr.stackalloc cmd.Length
            for i in 0..cmd.Length-1 do
                NativePtr.set ptrs i cmd.[i].Handle

            let mutable submit =
                VkSubmitInfo(
                    VkStructureType.SubmitInfo, 
                    0n,
                    0u, NativePtr.zero,
                    NativePtr.zero,
                    uint32 cmd.Length, ptrs,
                    0u, NativePtr.zero
                )

            let mutable fence = VkFence.Null
            let mutable fenceInfo =
                VkFenceCreateInfo(
                    VkStructureType.FenceCreateInfo, 
                    0n,
                    VkFenceCreateFlags.None
                )

            VkRaw.vkCreateFence(this.Device.Handle, &&fenceInfo, NativePtr.zero, &&fence)
                |> check "vkCreateFence"

            VkRaw.vkQueueSubmit(this.Handle, 1u, &&submit, fence)
                |> check "vkQueueSubmit"

            let wait() =
                let mutable fence = fence
                VkRaw.vkWaitForFences(this.Device.Handle, 1u, &&fence, 1u, ~~~0UL)
                    |> check "vkWaitForFences"

                VkRaw.vkDestroyFence(this.Device.Handle, fence, NativePtr.zero)

            wait
                
        start()

    [<Extension>]
    static member Submit(this : Queue, cmd : CommandBuffer[]) =
        async {
            let wait = submit(this, cmd)
            wait()
        }

    [<Extension>]
    static member SubmitAndWait(this : Queue, cmd : CommandBuffer[]) =
        let wait = submit(this, cmd)
        wait()

    [<Extension>]
    static member SubmitTask(this : Queue, cmd : CommandBuffer[]) =
        QueueCommandExtensions.Submit(this, cmd) |> Async.StartAsTask

    [<Extension>]
    static member WaitIdle(this : Queue) =
        VkRaw.vkQueueWaitIdle(this.Handle)
            |> check "vkQueueWaitIdle"


[<AbstractClass; Sealed; Extension>]
type QueuePoolCommandExtensions private() =

    [<Extension>]
    static member Submit(this : QueuePool, buffers : CommandBuffer[]) =
        async {
            let! q = this.AcquireAsync()
            try do! q.Submit(buffers)
            finally this.Release(q)
        }
       
    [<Extension>]
    static member SubmitAndWait(this : QueuePool, cmd : CommandBuffer[]) =
        let q = this.Acquire()
        q.SubmitAndWait(cmd)
        this.Release(q)

    [<Extension>]
    static member SubmitTask(this : QueuePool, cmd : CommandBuffer[]) =
        QueuePoolCommandExtensions.Submit(this, cmd) |> Async.StartAsTask
 