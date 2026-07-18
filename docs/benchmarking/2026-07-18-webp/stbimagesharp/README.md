# StbImageSharp replaces ImageMagick

## Summary

StbImageSharp was adopted after lowering PNG and JPEG decode p95, improving
complete `libwebp_full` production, and passing the warning-checked current-
target Native AOT publish.

## Example

![StbImageSharp 1600×900 output](../../assets/stbimagesharp-1600x900.webp)

[Capture fixture](capture-1600x900.lua) · [Raw log](capture-1600x900.log)

## Results

| Decoder | PNG p95 | JPEG p95 | Complete production p95 |
| --- | ---: | ---: | ---: |
| ImageMagick | 34.74 ms | 23.47 ms | 33.80 ms |
| StbImageSharp | 11.91 ms | 22.33 ms | 12.86 ms |

The component scrolling fixture produced 90 logical frames at 13.89 ms/frame
with 28.65 ms acquisition p95 and a 2.9 MB output.
