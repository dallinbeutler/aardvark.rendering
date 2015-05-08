﻿namespace Aardvark.Application.WPF

open System
open System.Windows
open System.Windows.Controls
open System.Windows.Media
open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Application
open System.Windows.Forms.Integration

type RenderControl() =
    inherit ContentControl()


    let mutable renderTask : Option<IRenderTask> = None
    let mutable impl : Option<IRenderTarget> = None
    let mutable ctrl : Option<FrameworkElement> = None

    let keyboard = new ChangeableKeyboard()
    let mouse = new Aardvark.Application.WinForms.Mouse()
    let sizes = new EventSource<V2i>()
    let mutable inner : Option<IMod<DateTime>> = None
    let time = 
        Mod.custom (fun () -> 
            match inner with
                | Some m -> m.GetValue()
                | None -> DateTime.Now
           )

    let setControl (self : RenderControl) (c : FrameworkElement) (cr : IRenderTarget) =
        match impl with
            | Some i -> failwith "implementation can only be set once per control"
            | None -> ()

        self.Content <- c


        match c with
            | :? WindowsFormsHost as host ->
                keyboard.Inner <- new Aardvark.Application.WinForms.Keyboard(host.Child)
                mouse.SetControl(host.Child)
                
            | _ ->
                failwith "impossible to create WPF mouse"

        match renderTask with
            | Some task -> cr.RenderTask <- task
            | None -> ()

        transact(fun () ->
            cr.Time.AddOutput(time)
            inner <- Some cr.Time
        )

        ctrl <- Some c
        impl <- Some cr

    override x.OnRenderSizeChanged(e) =
        base.OnRenderSizeChanged(e)
        sizes.Emit (V2i(base.ActualWidth, base.ActualHeight))

    member x.Implementation
        with get() = match ctrl with | Some c -> c | _ -> null
        and set v = setControl x v (v |> unbox<IRenderTarget>)


    member x.Sizes = sizes :> IEvent<V2i>

    member x.Keyboard = keyboard :> IKeyboard
    member x.Mouse = mouse :> IMouse

    member x.RenderTask 
        with get() = match renderTask with | Some t -> t | _ -> Unchecked.defaultof<_>
        and set t = 
            renderTask <- Some t
            match impl with
                | Some i -> i.RenderTask <- t
                | None -> ()

    member x.Time = time
    interface IRenderControl with
        member x.Sizes = x.Sizes
        member x.Time = time
        member x.Keyboard = x.Keyboard
        member x.Mouse = x.Mouse

        member x.RenderTask 
            with get() = x.RenderTask
            and set t = x.RenderTask <- t
    