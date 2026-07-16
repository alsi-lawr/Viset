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
    if animation.Count < 2 then
        failwithf "Expected a multi-frame WebP, got %d frame(s)" animation.Count

    if animation |> Seq.exists (fun frame -> not frame.HasAlpha) then
        failwith "WebP frames do not retain alpha"
finally
    animation.Dispose()
