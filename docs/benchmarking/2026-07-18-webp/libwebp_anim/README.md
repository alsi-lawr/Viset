# libwebp_anim halves size but misses the frame budget

## Summary

PNG source with `libwebp_anim`, spooled, lossy q75, method 0. The animation API
produces smaller output but is materially slower than `libwebp_full`, so it is
selectable but non-default.

## Example

![libwebp_anim 1600×900 output](../../assets/libwebp_anim-1600x900.webp)

[Capture fixture](capture-1600x900.lua) · [Raw log](capture-1600x900.log)

## Results

| Logical frames | Encoded frames | Acquisition p95 | Production/frame | Decode p95 | Encode p95 | Size |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 90 | 41 | 27.67 ms | 23.60 ms | 16.61 ms | 20.13 ms | 1.5 MB |

Twelve-frame complete-production p95: **30.65 ms**.
