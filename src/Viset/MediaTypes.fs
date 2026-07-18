namespace Viset

open System
open System.Globalization

type EncodedAnimation =
    { Bytes: byte array
      FrameTicksMs: int list
      Metrics: WebPProductionMetrics }

    override animation.ToString() =
        String.Concat(animation.FrameTicksMs.Length.ToString(CultureInfo.InvariantCulture), " frames")
