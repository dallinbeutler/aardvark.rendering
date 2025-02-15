﻿namespace Aardvark.Base


open System.Runtime.CompilerServices

[<AutoOpen>]
module FSharpPixImageCubeExtensions = 

    type PixImageCube with

        member x.Transformed (m : CubeSide -> CubeSide * ImageTrafo) =
            PixImageCube(
                x.MipMapArray 
                    |> Array.mapi (fun i mipMap -> 
                        let side = unbox<CubeSide> i
                        let (newSide, trafo) = m side
                        newSide, PixImageMipMap (mipMap.ImageArray |> Array.map (fun pi -> pi.Transformed(trafo)))
                    )
                    |> Map.ofArray
                    |> Map.toArray
                    |> Array.map snd
            )

        member x.Transformed (m : Map<CubeSide, CubeSide * ImageTrafo>) =
            x.Transformed (fun side ->
                match Map.tryFind side m with
                    | Some t -> t
                    | None -> 
                        Log.warn "incomplete CubeMap trafo"
                        (side, ImageTrafo.Rot0)

            )


        static member create (images : Map<CubeSide, PixImageMipMap>) =
            PixImageCube(images |> Map.toArray |> Array.map snd)

        static member create (images : Map<CubeSide, PixImage>) =
            PixImageCube(images |> Map.toArray |> Array.map (fun (_,pi) -> PixImageMipMap [|pi|]))

        static member create (images : Map<CubeSide, string>) =
            PixImageCube(images |> Map.toArray |> Array.map (fun (_,file) -> PixImageMipMap [|PixImage.Create file|]))

        static member create (images : Map<CubeSide, string>, loadOptions : PixLoadOptions) =
            PixImageCube(images |> Map.toArray |> Array.map (fun (_,file) -> PixImageMipMap [|PixImage.Create(file, loadOptions)|]))

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module PixImageCube =

        module Trafo = 
            let private figureOutComposeOp () =
            
                let v = Volume<byte>(V3i.III * 10)

                let direct = Array.zeroCreate 8
                for o0 in 0..7 do
                    direct.[o0] <- v.Transformed(unbox o0).Info

                for o0 in 0..7 do
                    for o1 in 0..7 do
                        let nv = v.Transformed(unbox o0).Transformed(unbox o1).Info

                        let res = direct |> Array.findIndex (fun di -> di.Equals(nv))

                        printfn "(ImageTrafo.%A, ImageTrafo.%A), ImageTrafo.%A" (unbox<ImageTrafo> o0) (unbox<ImageTrafo> o1) (unbox<ImageTrafo> res)

            let private composeTable =
                Dictionary.ofList [
                    (ImageTrafo.Rot0, ImageTrafo.Rot0), ImageTrafo.Rot0
                    (ImageTrafo.Rot0, ImageTrafo.Rot90), ImageTrafo.Rot90
                    (ImageTrafo.Rot0, ImageTrafo.Rot180), ImageTrafo.Rot180
                    (ImageTrafo.Rot0, ImageTrafo.Rot270), ImageTrafo.Rot270
                    (ImageTrafo.Rot0, ImageTrafo.MirrorX), ImageTrafo.MirrorX
                    (ImageTrafo.Rot0, ImageTrafo.Transpose), ImageTrafo.Transpose
                    (ImageTrafo.Rot0, ImageTrafo.MirrorY), ImageTrafo.MirrorY
                    (ImageTrafo.Rot0, ImageTrafo.Transverse), ImageTrafo.Transverse
                    (ImageTrafo.Rot90, ImageTrafo.Rot0), ImageTrafo.Rot90
                    (ImageTrafo.Rot90, ImageTrafo.Rot90), ImageTrafo.Rot180
                    (ImageTrafo.Rot90, ImageTrafo.Rot180), ImageTrafo.Rot270
                    (ImageTrafo.Rot90, ImageTrafo.Rot270), ImageTrafo.Rot0
                    (ImageTrafo.Rot90, ImageTrafo.MirrorX), ImageTrafo.Transverse
                    (ImageTrafo.Rot90, ImageTrafo.Transpose), ImageTrafo.MirrorX
                    (ImageTrafo.Rot90, ImageTrafo.MirrorY), ImageTrafo.Transpose
                    (ImageTrafo.Rot90, ImageTrafo.Transverse), ImageTrafo.MirrorY
                    (ImageTrafo.Rot180, ImageTrafo.Rot0), ImageTrafo.Rot180
                    (ImageTrafo.Rot180, ImageTrafo.Rot90), ImageTrafo.Rot270
                    (ImageTrafo.Rot180, ImageTrafo.Rot180), ImageTrafo.Rot0
                    (ImageTrafo.Rot180, ImageTrafo.Rot270), ImageTrafo.Rot90
                    (ImageTrafo.Rot180, ImageTrafo.MirrorX), ImageTrafo.MirrorY
                    (ImageTrafo.Rot180, ImageTrafo.Transpose), ImageTrafo.Transverse
                    (ImageTrafo.Rot180, ImageTrafo.MirrorY), ImageTrafo.MirrorX
                    (ImageTrafo.Rot180, ImageTrafo.Transverse), ImageTrafo.Transpose
                    (ImageTrafo.Rot270, ImageTrafo.Rot0), ImageTrafo.Rot270
                    (ImageTrafo.Rot270, ImageTrafo.Rot90), ImageTrafo.Rot0
                    (ImageTrafo.Rot270, ImageTrafo.Rot180), ImageTrafo.Rot90
                    (ImageTrafo.Rot270, ImageTrafo.Rot270), ImageTrafo.Rot180
                    (ImageTrafo.Rot270, ImageTrafo.MirrorX), ImageTrafo.Transpose
                    (ImageTrafo.Rot270, ImageTrafo.Transpose), ImageTrafo.MirrorY
                    (ImageTrafo.Rot270, ImageTrafo.MirrorY), ImageTrafo.Transverse
                    (ImageTrafo.Rot270, ImageTrafo.Transverse), ImageTrafo.MirrorX
                    (ImageTrafo.MirrorX, ImageTrafo.Rot0), ImageTrafo.MirrorX
                    (ImageTrafo.MirrorX, ImageTrafo.Rot90), ImageTrafo.Transpose
                    (ImageTrafo.MirrorX, ImageTrafo.Rot180), ImageTrafo.MirrorY
                    (ImageTrafo.MirrorX, ImageTrafo.Rot270), ImageTrafo.Transverse
                    (ImageTrafo.MirrorX, ImageTrafo.MirrorX), ImageTrafo.Rot0
                    (ImageTrafo.MirrorX, ImageTrafo.Transpose), ImageTrafo.Rot90
                    (ImageTrafo.MirrorX, ImageTrafo.MirrorY), ImageTrafo.Rot180
                    (ImageTrafo.MirrorX, ImageTrafo.Transverse), ImageTrafo.Rot270
                    (ImageTrafo.Transpose, ImageTrafo.Rot0), ImageTrafo.Transpose
                    (ImageTrafo.Transpose, ImageTrafo.Rot90), ImageTrafo.MirrorY
                    (ImageTrafo.Transpose, ImageTrafo.Rot180), ImageTrafo.Transverse
                    (ImageTrafo.Transpose, ImageTrafo.Rot270), ImageTrafo.MirrorX
                    (ImageTrafo.Transpose, ImageTrafo.MirrorX), ImageTrafo.Rot270
                    (ImageTrafo.Transpose, ImageTrafo.Transpose), ImageTrafo.Rot0
                    (ImageTrafo.Transpose, ImageTrafo.MirrorY), ImageTrafo.Rot90
                    (ImageTrafo.Transpose, ImageTrafo.Transverse), ImageTrafo.Rot180
                    (ImageTrafo.MirrorY, ImageTrafo.Rot0), ImageTrafo.MirrorY
                    (ImageTrafo.MirrorY, ImageTrafo.Rot90), ImageTrafo.Transverse
                    (ImageTrafo.MirrorY, ImageTrafo.Rot180), ImageTrafo.MirrorX
                    (ImageTrafo.MirrorY, ImageTrafo.Rot270), ImageTrafo.Transpose
                    (ImageTrafo.MirrorY, ImageTrafo.MirrorX), ImageTrafo.Rot180
                    (ImageTrafo.MirrorY, ImageTrafo.Transpose), ImageTrafo.Rot270
                    (ImageTrafo.MirrorY, ImageTrafo.MirrorY), ImageTrafo.Rot0
                    (ImageTrafo.MirrorY, ImageTrafo.Transverse), ImageTrafo.Rot90
                    (ImageTrafo.Transverse, ImageTrafo.Rot0), ImageTrafo.Transverse
                    (ImageTrafo.Transverse, ImageTrafo.Rot90), ImageTrafo.MirrorX
                    (ImageTrafo.Transverse, ImageTrafo.Rot180), ImageTrafo.Transpose
                    (ImageTrafo.Transverse, ImageTrafo.Rot270), ImageTrafo.MirrorY
                    (ImageTrafo.Transverse, ImageTrafo.MirrorX), ImageTrafo.Rot90
                    (ImageTrafo.Transverse, ImageTrafo.Transpose), ImageTrafo.Rot180
                    (ImageTrafo.Transverse, ImageTrafo.MirrorY), ImageTrafo.Rot270
                    (ImageTrafo.Transverse, ImageTrafo.Transverse), ImageTrafo.Rot0
                ]

            let private composeTrafo (l : ImageTrafo) (r : ImageTrafo) =
                composeTable.[(l,r)]

            let private identity =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.Rot0)
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.Rot0)
                    CubeSide.PositiveZ, (CubeSide.PositiveZ, ImageTrafo.Rot0)
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.Rot0)
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.Rot0)
                    CubeSide.NegativeZ, (CubeSide.NegativeZ, ImageTrafo.Rot0)
                ]

            let private compose (l : Map<CubeSide, CubeSide * ImageTrafo>) (r : Map<CubeSide, CubeSide * ImageTrafo>) : Map<CubeSide, CubeSide * ImageTrafo> =
                l |> Map.map (fun s (ts, lt) ->
                    match Map.tryFind ts r with
                        | Some (fs, rt) -> fs, composeTrafo lt rt
                        | None -> ts, lt
                )

            let private all (l : seq<Map<CubeSide, CubeSide * ImageTrafo>>) =
                l |> Seq.fold compose identity

            let RotX90 =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.Rot90)   
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.Rot270)   
                    CubeSide.PositiveZ, (CubeSide.NegativeY, ImageTrafo.Rot0)   
                    CubeSide.NegativeZ, (CubeSide.PositiveY, ImageTrafo.Rot180)   
                    CubeSide.PositiveY, (CubeSide.PositiveZ, ImageTrafo.Rot0)   
                    CubeSide.NegativeY, (CubeSide.NegativeZ, ImageTrafo.Rot180)   
                ]

            let RotX180 = compose RotX90 RotX90
            let RotX270 = compose RotX180 RotX90


            let RotY90 =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.NegativeZ, ImageTrafo.Rot0)   
                    CubeSide.NegativeX, (CubeSide.PositiveZ, ImageTrafo.Rot0)   
                    CubeSide.PositiveZ, (CubeSide.PositiveX, ImageTrafo.Rot0)   
                    CubeSide.NegativeZ, (CubeSide.NegativeX, ImageTrafo.Rot0)   
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.Rot270)   
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.Rot90)   
                ]

            let RotY180 = compose RotY90 RotY90
            let RotY270 = compose RotY180 RotY90




            let RotZ90 =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveY, ImageTrafo.Rot90)   
                    CubeSide.NegativeX, (CubeSide.NegativeY, ImageTrafo.Rot90)   
                    CubeSide.PositiveZ, (CubeSide.PositiveZ, ImageTrafo.Rot90)   
                    CubeSide.NegativeZ, (CubeSide.NegativeZ, ImageTrafo.Rot270)   
                    CubeSide.PositiveY, (CubeSide.NegativeX, ImageTrafo.Rot90)   
                    CubeSide.NegativeY, (CubeSide.PositiveX, ImageTrafo.Rot90)   
                ]

            let RotZ180 = compose RotZ90 RotZ90
            let RotZ270 = compose RotZ180 RotZ90



            let InvertX =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.NegativeX, ImageTrafo.MirrorX)   
                    CubeSide.NegativeX, (CubeSide.PositiveX, ImageTrafo.MirrorX)   
                    CubeSide.PositiveZ, (CubeSide.PositiveZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeZ, (CubeSide.NegativeZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.MirrorX)   
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.MirrorX)   
                ]

            let InvertY =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.MirrorY)
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.MirrorY)
                    CubeSide.PositiveY, (CubeSide.NegativeY, ImageTrafo.MirrorY)
                    CubeSide.NegativeY, (CubeSide.PositiveY, ImageTrafo.MirrorY)
                    CubeSide.PositiveZ, (CubeSide.PositiveZ, ImageTrafo.MirrorY)
                    CubeSide.NegativeZ, (CubeSide.NegativeZ, ImageTrafo.MirrorY)
                ]

            let InvertZ =
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.MirrorX)   
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.MirrorX)   
                    CubeSide.PositiveZ, (CubeSide.NegativeZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeZ, (CubeSide.PositiveZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.MirrorY)   
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.MirrorY)   
                ]

            let OfOpenGlConventionTrafo = 
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.MirrorX)   
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.MirrorX)   
                    CubeSide.PositiveZ, (CubeSide.NegativeZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeZ, (CubeSide.PositiveZ, ImageTrafo.MirrorX)   
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.MirrorY)   
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.MirrorY)   
                ]

            let ToOpenGlConventionTrafo = 
                Map.ofList [
                    CubeSide.PositiveX, (CubeSide.PositiveX, ImageTrafo.MirrorY)   
                    CubeSide.NegativeX, (CubeSide.NegativeX, ImageTrafo.MirrorY)   
                    CubeSide.PositiveZ, (CubeSide.NegativeZ, ImageTrafo.Rot180)   
                    CubeSide.NegativeZ, (CubeSide.PositiveZ, ImageTrafo.Rot180)   
                    CubeSide.PositiveY, (CubeSide.PositiveY, ImageTrafo.Rot0)   
                    CubeSide.NegativeY, (CubeSide.NegativeY, ImageTrafo.Rot0)   
                ]

        let load (faceFiles : list<CubeSide * string>) =
            let faces = 
                faceFiles 
                    |> List.map (fun (s,file) -> s, PixImageMipMap [| PixImage.Create(file) |]) 
                    |> List.sortBy fst 
                    |> List.map snd 
                    |> List.toArray

            PixImageCube(faces)

        let rotX90 (c : PixImageCube) =
            c.Transformed Trafo.RotX90

        let rotX180 (c : PixImageCube) =
            c.Transformed Trafo.RotX180

        let rotX270 (c : PixImageCube) =
            c.Transformed Trafo.RotX270

        let rotY90 (c : PixImageCube) =
            c.Transformed Trafo.RotY90

        let rotY180 (c : PixImageCube) =
            c.Transformed Trafo.RotY180

        let rotY270 (c : PixImageCube) =
            c.Transformed Trafo.RotY270

        let rotZ90 (c : PixImageCube) =
            c.Transformed Trafo.RotZ90

        let rotZ180 (c : PixImageCube) =
            c.Transformed Trafo.RotZ180

        let rotZ270 (c : PixImageCube) =
            c.Transformed Trafo.RotZ270

        let invertX (c : PixImageCube) =
            c.Transformed Trafo.InvertX

        let invertY (c : PixImageCube) =
            c.Transformed Trafo.InvertY

        let invertZ (c : PixImageCube) =
            c.Transformed Trafo.InvertZ


        let ofOpenGlConvention (c : PixImageCube) =
            c.Transformed Trafo.OfOpenGlConventionTrafo
            
        let toOpenGlConvention (c : PixImageCube) =
            c.Transformed Trafo.ToOpenGlConventionTrafo
            
        let toTexture (mipMaps : bool) (c : PixImageCube) =
            PixTextureCube(c, mipMaps) :> ITexture

[<AbstractClass; Sealed; Extension>]
type PixImageCubeExtensions private() =

    [<Extension>]
    static member Transformed(x : PixImageCube, f : System.Func<CubeSide, Tup<CubeSide, ImageTrafo>>) =
        x.Transformed(fun side ->
            let t = f.Invoke(side)
            t.E0, t.E1
        )
   
    [<Extension>]
    static member Transformed(x : PixImageCube, d : System.Collections.Generic.Dictionary<CubeSide, Tup<CubeSide, ImageTrafo>>) =
        x.Transformed (fun side ->
            match d.TryGetValue side with
                | (true, t) -> t.E0, t.E1
                | _ ->
                    Log.warn "incomplete CubeMap trafo" 
                    side, ImageTrafo.Rot0
        )
   

    [<Extension>] static member RotX90(x : PixImageCube) = PixImageCube.rotX90 x
    [<Extension>] static member RotX180(x : PixImageCube) = PixImageCube.rotX180 x
    [<Extension>] static member RotX270(x : PixImageCube) = PixImageCube.rotX270 x

    [<Extension>] static member RotY90(x : PixImageCube) = PixImageCube.rotY90 x
    [<Extension>] static member RotY180(x : PixImageCube) = PixImageCube.rotY180 x
    [<Extension>] static member RotY270(x : PixImageCube) = PixImageCube.rotY270 x  

    [<Extension>] static member RotZ90(x : PixImageCube) = PixImageCube.rotZ90 x
    [<Extension>] static member RotZ180(x : PixImageCube) = PixImageCube.rotZ180 x
    [<Extension>] static member RotZ270(x : PixImageCube) = PixImageCube.rotZ270 x  

    [<Extension>] static member InvertX(x : PixImageCube) = PixImageCube.invertX x
    [<Extension>] static member InvertY(x : PixImageCube) = PixImageCube.invertY x
    [<Extension>] static member InvertZ(x : PixImageCube) = PixImageCube.invertZ x  
    [<Extension>] static member FromOpenGlConvention(x : PixImageCube) = PixImageCube.ofOpenGlConvention x  
    [<Extension>] static member ToOpenGlConvention(x : PixImageCube) = PixImageCube.toOpenGlConvention x  