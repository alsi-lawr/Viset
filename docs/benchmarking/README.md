# Benchmarking Viset WebP

## Summary

Viset benchmarks 1600×900 acquisition, decoding, encoding, and complete capture
as separate boundaries. Sources, encoders, and pipelines are independently
selectable; the retained defaults are PNG screencast, `libwebp_full`, spooled,
lossy quality 75, and method 0.

## Example

```toml
[webp]
source = "jpeg_screencast"
source_quality = 95
encoder = "libwebp_anim"
pipeline = "live"
mode = "lossy"
quality = 75
method = 0
```

Valid sources are `png_screencast` and `jpeg_screencast`; encoders are
`libwebp_full`, `libwebp_anim`, and unbundled `ffmpeg`; pipelines are `spooled`
and `live`; modes are `lossy` and `lossless`.

## Results

| Boundary | Best current result | Outcome |
| --- | ---: | --- |
| 60 FPS acquisition | JPEG q95: 18.40 ms p95 | Above the 16.67 ms frame interval |
| Complete production | `libwebp_full`: 12.18 ms p95 | Fastest encoder |
| Pipeline production | Live: 12.83 ms/frame | Slightly faster, not promoted |
| PNG decode | StbImageSharp: 11.91 ms p95 | Adopted over ImageMagick |

The [2026-07-18 composable WebP record](2026-07-18-webp/) contains the full
measurements, raw evidence, component examples, and reproduction fixtures.
