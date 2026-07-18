namespace Viset.Tests

open System
open FsUnit
open NUnit.Framework
open Viset

module PerformanceMetricsTests =
    [<Test>]
    let ``capture observations should produce the public capture metrics`` () =
        let metrics =
            PerformanceMetrics.capture
                { Source = PngScreencast
                  Pipeline = Spooled
                  FrameCount = 3
                  UniqueFrameCount = 2
                  ActiveDuration = TimeSpan.FromMilliseconds 100.0
                  CaptureDurations = [ TimeSpan.FromMilliseconds 1.0 ]
                  MissedSlots = 1
                  DuplicatedFrames = 1
                  DroppedFrames = 0 }

        metrics.FrameCount |> should equal 3
        metrics.MissedSlots |> should equal 1

    [<Test>]
    let ``production observations should preserve backend-specific metric values`` () =
        let metrics =
            PerformanceMetrics.webP
                { Encoder = LibWebPFull
                  Pipeline = Live
                  FrameCount = 3
                  EncodedFrameCount = 2
                  SpilledFrameCount = 1
                  WorkerCount = 2
                  DecodeDurations = []
                  EncodeDurations = []
                  MuxDuration = TimeSpan.FromMilliseconds 1.0
                  TotalDuration = TimeSpan.FromMilliseconds 2.0 }

        metrics.EncodedFrameCount |> should equal 2
        metrics.SpilledFrameCount |> should equal 1
