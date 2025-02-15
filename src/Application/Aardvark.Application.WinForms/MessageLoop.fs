﻿namespace Aardvark.Application.WinForms


open System
open System.Diagnostics
open System.Windows.Forms
open System.Threading
open System.Collections.Concurrent
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Application

type IControl =
    abstract member Paint : unit -> unit
    abstract member Invalidate : unit -> unit
    abstract member Invoke : (unit -> unit) -> unit
    abstract member IsInvalid : bool

//
//type Periodic(interval : int, f : float -> unit) =
//    let times = RunningMean(100)
//    let sw = Stopwatch()
//
//    member x.RunIfNeeded() =
//        if not sw.IsRunning then
//            sw.Start()
//        else
//            let dt = sw.Elapsed.TotalMilliseconds
//               
//            if interval = 0 || dt >= float interval then
//                times.Add dt
//                sw.Restart()
//                f(times.Average / 1000.0)
//
//
//[<AllowNullLiteral>]
//type MyTimer(f : unit -> unit, due : int64, interval : int64) =
//    
//    let sw = Stopwatch()
//    let cancel = new CancellationTokenSource()
//
//    let run() =
//        let ct = cancel.Token
//        try
//            Thread.Sleep(int due)
//            while true do
//                ct.ThrowIfCancellationRequested()
//                sw.Restart()
//                f()
//                sw.Stop()
//                let t = sw.Elapsed.TotalMilliseconds |> int64
//                let sleep = max 0L (interval - t) |> int
//                Thread.Sleep(sleep)
//
//        with :? OperationCanceledException ->
//            Log.line "Timer cancelled"
//            ()
//
//    let thread = Thread(ThreadStart(run), Priority = ThreadPriority.Highest, IsBackground = true)
//    do thread.Start()
//
//    member x.Dispose() =
//        cancel.Cancel()
//        thread.Join()
//        cancel.Dispose()
//
//type MessageLoop() as this =
//
//
//
//    static let rec interlockedChange (location : byref<'a>) (update : 'a -> 'a) =
//        let mutable oldValue = location
//        let newValue = update oldValue
//        let mutable ex = Interlocked.CompareExchange(&location, newValue, oldValue)
//        while not <| System.Object.ReferenceEquals(ex, oldValue) do
//            oldValue <- ex
//            let newValue = update oldValue
//            ex <- Interlocked.CompareExchange(&location, newValue, oldValue)
//
//    let mutable q : hset<IControl> = HSet.empty
//    let mutable timer : MyTimer = null
//    let periodic = ConcurrentHashSet<Periodic>()
//
//    let rec processAll() =
//        let mine = Interlocked.Exchange(&q, HSet.empty)
//        let mine = mine |> HSet.toList
//        for ctrl in mine do
//            try 
//                if not ctrl.IsInvalid then
//                    ctrl.Invoke (fun () -> ctrl.Invalidate())
//            with e ->
//                printfn "%A" e
//
//    member private x.Process() =
//        //Application.DoEvents()
//        //for p in periodic do p.RunIfNeeded()
//        processAll()
//        //Application.DoEvents()
//
//    member x.Start() =
//        if timer <> null then
//            timer.Dispose()
//
//    
//        timer <- new MyTimer((fun _ -> this.Process()), 0L, 2L)
//
//    member x.Draw(c : IControl) =
//        interlockedChange &q (fun q -> HSet.add c q)
//
//    member x.EnqueuePeriodic (f : float -> unit, intervalInMilliseconds : int) =
//        let p = Periodic(intervalInMilliseconds, f)
//        periodic.Add p |> ignore
//
//        { new IDisposable with
//            member x.Dispose() =
//                periodic.Remove p |> ignore
//        }
//            
//    member x.EnqueuePeriodic (f : float -> unit) =
//        x.EnqueuePeriodic(f, 1)
//

type IInvalidateControl =
    abstract member IsInvalid : bool

type private MessageLoopImpl() =
    let mutable running = true
    let trigger = new MultimediaTimer.Trigger(1)
    
    let stopwatch = System.Diagnostics.Stopwatch()
    let mean = RunningMean(10)

    [<VolatileField>]
    let mutable dirty : ref<hset<Control>> = ref HSet.empty

    let run() =
        while running do
            trigger.Wait()
            let set = !Interlocked.Exchange(&dirty, ref HSet.empty)
            if not (HSet.isEmpty set) then
                let controls = set |> Seq.filter (fun c -> not c.IsDisposed && c.IsHandleCreated) |> Seq.toList
                match controls with
                    | h :: _ ->
                        stopwatch.Restart()
                        h.Invoke(new System.Action(fun () ->
                            for ctrl in controls do 
                                let ic = unbox<IInvalidateControl> ctrl
                                if not ic.IsInvalid then
                                    ctrl.Refresh()
                        )) |> ignore
                        stopwatch.Stop()

                        mean.Add stopwatch.Elapsed.TotalSeconds

                    | [] ->
                        ()

    let thread = Thread(ThreadStart(run), IsBackground = true)
    do thread.Start()

    member x.FrameTime = MicroTime(int64 (1000000000.0 * mean.Average))

    member x.Invalidate(ctrl : Control) =
        let mutable contained = false
        let mutable o = dirty
        let mutable n = ref <| HSet.alter ctrl (fun c -> contained <- c; true) !o
        while Interlocked.CompareExchange(&dirty, n, o) != o do
            o <- dirty
            n <- ref <| HSet.alter ctrl (fun c -> contained <- c; true) !o

        not contained

type MessageLoop private() =
    static let loop = lazy (MessageLoopImpl())

    static member FrameTime = loop.Value.FrameTime
    static member Invalidate(ctrl : Control) = loop.Value.Invalidate(ctrl)


