namespace Viset

open System

type internal CaptureMetricObservations =
    { Source: WebPSource
      Pipeline: WebPPipeline
      FrameCount: int
      UniqueFrameCount: int
      ActiveDuration: TimeSpan
      CaptureDurations: TimeSpan list
      MissedSlots: int
      DuplicatedFrames: int
      DroppedFrames: int }

type internal WebPProductionObservations =
    { Encoder: WebPEncoder
      Pipeline: WebPPipeline
      FrameCount: int
      EncodedFrameCount: int
      SpilledFrameCount: int
      WorkerCount: int
      DecodeDurations: TimeSpan list
      EncodeDurations: TimeSpan list
      MuxDuration: TimeSpan
      TotalDuration: TimeSpan }

module internal PerformanceMetrics =
    let capture (observations: CaptureMetricObservations) : CapturePerformanceMetrics =
        { Source = observations.Source
          Pipeline = observations.Pipeline
          FrameCount = observations.FrameCount
          UniqueFrameCount = observations.UniqueFrameCount
          ActiveDuration = observations.ActiveDuration
          CaptureDurations = observations.CaptureDurations
          MissedSlots = observations.MissedSlots
          DuplicatedFrames = observations.DuplicatedFrames
          DroppedFrames = observations.DroppedFrames }

    let webP (observations: WebPProductionObservations) : WebPProductionMetrics =
        { Encoder = observations.Encoder
          Pipeline = observations.Pipeline
          FrameCount = observations.FrameCount
          EncodedFrameCount = observations.EncodedFrameCount
          SpilledFrameCount = observations.SpilledFrameCount
          WorkerCount = observations.WorkerCount
          DecodeDurations = observations.DecodeDurations
          EncodeDurations = observations.EncodeDurations
          MuxDuration = observations.MuxDuration
          TotalDuration = observations.TotalDuration }
