# FFmpeg is compact but slower than libwebp_full

## Summary

PNG source with FFmpeg `libwebp_anim`, spooled, lossy q75, method 0. FFmpeg is
unbundled, validated before capture, and normalizes final duration to the
logical timeline.

## Example

![FFmpeg 1600×900 output](../../assets/ffmpeg-1600x900.webp)

[Capture fixture](capture-1600x900.lua) · [Raw log](capture-1600x900.log)

## Results

| Logical frames | Encoded frames | Acquisition p95 | Production/frame | Size |
| ---: | ---: | ---: | ---: | ---: |
| 90 | 43 | 31.93 ms | 14.17 ms | 1.5 MB |

Twelve-frame complete-production p95, including startup and pipe I/O:
**22.78 ms**.
