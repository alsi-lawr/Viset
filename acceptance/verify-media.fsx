open System
open System.IO
open ImageMagick

let root = Path.GetFullPath fsi.CommandLineArgs[1]

let pngPaths =
    [ Path.Combine(root, "screenshots", "red.png")
      Path.Combine(root, "screenshots", "blue.png") ]

for path in pngPaths do
    use image = new MagickImage(path)

    if not image.HasAlpha then
        failwithf "PNG does not retain alpha: %s" path

    let corner = image.GetPixels().GetPixel(0, 0).ToColor()

    if corner.A <> 0uy then
        failwithf "PNG corner is not transparent: %s" path

let webpPath = Path.Combine(root, "animations", "motion.webp")
let animation = new MagickImageCollection(webpPath)

try
    if animation.Count <> 4 then
        failwithf "Expected four WebP frames, got %d" animation.Count

    let delays =
        animation
        |> Seq.map (fun frame -> int frame.AnimationDelay, frame.AnimationTicksPerSecond, frame.HasAlpha)
        |> Seq.toList

    // ImageMagick normalises decoded WebP timing to centiseconds. The fixture's
    // Python verifier checks the exact millisecond ANMF duration fields.
    let expected = [ 3, 100, true; 3, 100, true; 3, 100, true; 3, 100, true ]

    if delays <> expected then
        failwithf "Unexpected WebP timing/alpha: %A" delays
finally
    animation.Dispose()
