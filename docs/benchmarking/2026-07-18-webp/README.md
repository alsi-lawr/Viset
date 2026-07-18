# Composable WebP: encoding is fast enough; acquisition limits strict 60 FPS

## Summary

At 1600×900, `libwebp_full` is the only encoder with complete-production p95
below the 16.67 ms frame interval. Neither PNG nor JPEG screencast acquisition
meets that threshold reliably. PNG, `libwebp_full`, and spooled remain the
defaults; StbImageSharp replaces ImageMagick.

Measurements are local evidence from a Ryzen 7 5700X3D running NixOS 26.11,
.NET 10.0.9, Chromium 150.0.7871.114, libwebp 1.6.0, and FFmpeg 8.1.1. The
machine was not isolated from desktop load. Outputs use lossy quality 75 and
method 0.

## Example

| Component | Report | 1600×900 output |
| --- | --- | --- |
| PNG screencast | [report](png_screencast/) | [WebP](../assets/png_screencast-1600x900.webp) |
| JPEG screencast | [report](jpeg_screencast/) | [WebP](../assets/jpeg_screencast-1600x900.webp) |
| `libwebp_full` | [report](libwebp_full/) | [WebP](../assets/libwebp_full-1600x900.webp) |
| `libwebp_anim` | [report](libwebp_anim/) | [WebP](../assets/libwebp_anim-1600x900.webp) |
| FFmpeg | [report](ffmpeg/) | [WebP](../assets/ffmpeg-1600x900.webp) |
| Spooled pipeline | [report](spooled/) | [WebP](../assets/spooled-1600x900.webp) |
| Live pipeline | [report](live/) | [WebP](../assets/live-1600x900.webp) |
| StbImageSharp | [report](stbimagesharp/) | [WebP](../assets/stbimagesharp-1600x900.webp) |

These fixtures isolate components; they do not claim every source/encoder/
pipeline cross-product was benchmarked.

## Results

Twelve unique frames, normalised per logical frame:

| Method | Mean | p95 | Maximum |
| --- | ---: | ---: | ---: |
| ImageMagick PNG decode | 34.50 ms | 34.74 ms | 34.78 ms |
| StbImageSharp PNG decode | 11.83 ms | 11.91 ms | 11.93 ms |
| ImageMagick JPEG decode | 23.31 ms | 23.47 ms | 23.47 ms |
| StbImageSharp JPEG decode | 22.02 ms | 22.33 ms | 22.37 ms |
| `libwebp_full` complete production | 11.56 ms | 12.18 ms | 12.28 ms |
| `libwebp_anim` complete production | 30.36 ms | 30.65 ms | 30.68 ms |
| FFmpeg complete production | 22.46 ms | 22.78 ms | 22.82 ms |

Three-second browser acquisition runs:

| Source | FPS | Target | Emitted | p95 | p99 | Maximum |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| PNG screencast | 30 | 90 | 131 | 40.08 ms | 43.38 ms | 85.02 ms |
| JPEG screencast q95 | 30 | 90 | 180 | 18.34 ms | 20.32 ms | 42.80 ms |
| PNG screencast | 60 | 180 | 135 | 40.41 ms | 45.83 ms | 68.55 ms |
| JPEG screencast q95 | 60 | 180 | 181 | 18.40 ms | 19.33 ms | 24.81 ms |

- Duplicate-heavy scrolling: PNG was 32.77 ms acquisition p95 and 12.05
  ms/frame production; JPEG was 35.69 ms and 12.74 ms/frame. JPEG was not
  promoted.
- StbImageSharp improved controlled complete production from 33.80 ms to 12.86
  ms p95 and passed current-target Native AOT.
- Coalescing reduced twelve logical frames to two encoded frames while
  preserving 400 ms. The slow live fixture spilled ten frames and preserved
  400 ms.
- Browser WebP remains rejected: q75 was 88.51 ms p95 and q95 was 101.54 ms,
  versus PNG at 52.45 ms.

Raw evidence: [BenchmarkDotNet JSON](raw/benchmarkdotnet-composable-current.json),
[benchmark log](raw/benchmarkdotnet-composable-current.log),
[acquisition comparison](raw/acquisition-comparison-1600x900.txt), and
[browser-WebP probe](raw/browser-webp-probe.txt).
