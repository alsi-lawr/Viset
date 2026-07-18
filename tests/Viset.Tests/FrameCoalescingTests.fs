namespace Viset.Tests

open System
open FsUnit
open NUnit.Framework
open Viset

module FrameCoalescingTests =
    let private frame format bytes = { Format = format; Bytes = bytes }

    [<Test>]
    let ``equal consecutive source bytes should coalesce into one run`` () =
        let coalescing = FrameCoalescing.start (frame PngImage [| 1uy; 2uy |]) 34

        let coalescing, emitted =
            FrameCoalescing.step (frame PngImage [| 1uy; 2uy |]) 33 coalescing

        let coalescing, completed =
            FrameCoalescing.step (frame PngImage [| 3uy |]) 33 coalescing

        let first =
            completed
            |> Option.defaultWith (fun () -> failwith "Expected a completed source run.")

        let second = FrameCoalescing.finish coalescing

        emitted |> should equal None
        first.Sequence |> should equal 0
        first.Duration |> should equal 67L
        second.Sequence |> should equal 1
        second.Duration |> should equal 33L

    [<Test>]
    let ``matching bytes in different source formats should start a new run`` () =
        let bytes = [| 1uy; 2uy |]
        let coalescing = FrameCoalescing.start (frame PngImage bytes) 17
        let _, completed = FrameCoalescing.step (frame JpegImage bytes) 16 coalescing

        completed.IsSome |> should equal true

    [<Test>]
    let ``large coalesced durations should use checked wide arithmetic`` () =
        let source = frame PngImage [| 4uy |]
        let coalescing = FrameCoalescing.start source Int32.MaxValue
        let coalescing, emitted = FrameCoalescing.step source Int32.MaxValue coalescing

        emitted |> should equal None
        (FrameCoalescing.finish coalescing).Duration |> should equal 4294967294L

    [<Test>]
    let ``splitting a duration should preserve every millisecond within native limits`` () =
        let maximumDuration = WebPEncoding.MaximumFrameDurationMilliseconds

        FrameCoalescing.splitDuration 50 67L |> should equal [ 50; 17 ]

        FrameCoalescing.splitDuration maximumDuration (int64 maximumDuration + 1L)
        |> should equal [ maximumDuration; 1 ]
