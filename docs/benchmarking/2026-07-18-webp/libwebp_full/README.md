# libwebp_full is the fastest encoder

## Summary

PNG source with `libwebp_full`, spooled, lossy q75, method 0. Four workers
encode full frames, mux them in order, and coalesce exact consecutive source
bytes. This remains the encoder default.

## Example

![libwebp_full 1600×900 output](../../assets/libwebp_full-1600x900.webp)

[Capture fixture](capture-1600x900.lua) · [Raw log](capture-1600x900.log)

## Results

| Logical frames | Encoded frames | Acquisition p95 | Production/frame | Decode p95 | Encode p95 | Size |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 90 | 43 | 33.41 ms | 13.14 ms | 21.97 ms | 95.09 ms | 2.9 MB |

Twelve-frame complete-production p95: **12.18 ms**.
