namespace Viset.Tests

open System
open FsUnit
open NUnit.Framework
open Viset
open Viset.Tests.TestSupport

module WebPContainerTests =
    [<Test>]
    let ``parsing an animated container should count frames without mutating bytes`` () =
        let webP = animatedWebP ()
        let original = Array.copy webP
        let container = WebPContainer.parse webP

        WebPContainer.frameCount container |> should equal 2
        webP |> should equal original

    [<Test>]
    let ``planning a duration patch should correct only the final frame`` () =
        let webP = animatedWebP ()
        let container = WebPContainer.parse webP

        let patch =
            WebPContainer.durationPatch WebPEncoding.MaximumFrameDurationMilliseconds 40 container
            |> Option.defaultWith (fun () -> failwith "Expected a duration patch.")

        patch.Duration |> should equal 30
        int webP[patch.Offset] |> should equal 20

    [<Test>]
    let ``an invalid RIFF size should return an explicit diagnostic`` () =
        let webP = animatedWebP ()
        writeUInt32LittleEndian webP 4 0u

        (fun () -> WebPContainer.parse webP |> ignore)
        |> shouldFailWithMessage "An encoder returned a WebP container with an invalid RIFF size."

    [<Test>]
    let ``a truncated WebP chunk should return an explicit diagnostic`` () =
        let webP = animatedWebP ()
        writeUInt32LittleEndian webP 16 UInt32.MaxValue

        (fun () -> WebPContainer.parse webP |> ignore)
        |> shouldFailWithMessage "An encoder returned a truncated WebP chunk."

    [<Test>]
    let ``a WebP container without an image should return an explicit diagnostic`` () =
        let webP = webPWithoutImageFrame ()

        (fun () -> webP |> WebPContainer.parse |> WebPContainer.frameCount |> ignore)
        |> shouldFailWithMessage "An encoder returned a WebP container without an image frame."
