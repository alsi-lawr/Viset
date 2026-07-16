# Medium Viset example

This project captures a small dashboard across desktop/phone and light/dark axes. `screenshots.lua` produces four PNGs, while `activity.lua` produces two continuous WebP recordings. Each file contains its own TOML header and imperative Lua capture.

The screenshots use `frame = "builtin:auto"`, which selects Viset's built-in laptop or phone frame from the chosen device. The available selectors are `builtin:auto`, `builtin:phone`, and `builtin:laptop`.

From the Viset repository root, generate all outputs:

```sh
viset capture examples/medium/screenshots.lua --force
viset capture examples/medium/activity.lua --force
```

Open all four PNGs and both WebPs:

```sh
for file in \
  examples/medium/output/screenshots/desktop-light.png \
  examples/medium/output/screenshots/desktop-dark.png \
  examples/medium/output/screenshots/phone-light.png \
  examples/medium/output/screenshots/phone-dark.png \
  examples/medium/output/animations/desktop-light-activity.webp \
  examples/medium/output/animations/desktop-dark-activity.webp
do
  xdg-open "$file"
done
```

Generated captures:

![Desktop light capture](output/screenshots/desktop-light.png)
![Desktop dark capture](output/screenshots/desktop-dark.png)
![Phone light capture](output/screenshots/phone-light.png)
![Phone dark capture](output/screenshots/phone-dark.png)

- [Desktop light activity recording](output/animations/desktop-light-activity.webp)
- [Desktop dark activity recording](output/animations/desktop-dark-activity.webp)

Capture files are trusted local Lua code and run with Lua's standard libraries.
