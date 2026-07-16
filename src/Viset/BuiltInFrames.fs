namespace Viset

open System

module BuiltInFrames =
    let private source =
        """<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Viset built-in device frame</title>
    <style>
      * {
        box-sizing: border-box;
      }

      html,
      body {
        width: 100%;
        height: 100%;
        margin: 0;
        overflow: hidden;
        background: transparent;
      }

      body {
        display: grid;
        place-items: center;
      }

      .hardware {
        position: relative;
        transform-origin: center;
      }

      .screen {
        position: absolute;
        overflow: hidden;
        background: #050608;
      }

      .screen img {
        display: block;
        width: 100%;
        height: 100%;
      }

      .phone {
        padding: 29px 14px;
        border: 1px solid #656b75;
        border-radius: 42px;
        background: linear-gradient(145deg, #555b65 0%, #171a20 9%, #080a0d 58%, #343941 100%);
        box-shadow:
          0 16px 28px rgb(0 0 0 / 26%),
          inset 0 0 0 2px #0a0c0f,
          inset 0 0 0 3px rgb(255 255 255 / 7%);
      }

      .phone .screen {
        top: 29px;
        left: 14px;
        border-radius: 29px;
        box-shadow: 0 0 0 1px #020304;
      }

      .phone .speaker {
        position: absolute;
        top: 12px;
        left: 50%;
        width: 48px;
        height: 5px;
        border-radius: 999px;
        background: #030405;
        transform: translateX(-50%);
        box-shadow: inset 0 1px 1px rgb(255 255 255 / 12%);
      }

      .phone .camera {
        position: absolute;
        top: 11px;
        left: calc(50% + 34px);
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: radial-gradient(circle at 35% 35%, #375479, #080b11 62%);
        box-shadow: 0 0 0 1px #020304;
      }

      .phone .button {
        position: absolute;
        width: 4px;
        border-radius: 3px;
        background: linear-gradient(90deg, #16191e, #6b717a);
      }

      .phone .button-power {
        top: 116px;
        right: -5px;
        height: 72px;
      }

      .phone .button-volume-up {
        top: 102px;
        left: -5px;
        height: 48px;
      }

      .phone .button-volume-down {
        top: 160px;
        left: -5px;
        height: 48px;
      }

      .laptop .lid {
        position: absolute;
        top: 0;
        left: 50%;
        padding: 14px;
        border: 1px solid #555b64;
        border-radius: 15px 15px 7px 7px;
        background: linear-gradient(145deg, #343941, #111419 18%, #090b0e 75%, #292e35);
        box-shadow:
          0 18px 28px rgb(0 0 0 / 22%),
          inset 0 0 0 1px rgb(255 255 255 / 5%);
        transform: translateX(-50%);
      }

      .laptop .screen {
        top: 14px;
        left: 14px;
        border-radius: 5px;
        box-shadow: 0 0 0 1px #020304;
      }

      .laptop .camera {
        position: absolute;
        top: 5px;
        left: 50%;
        width: 5px;
        height: 5px;
        border-radius: 50%;
        background: #050608;
        box-shadow: inset 0 0 0 1px #252b34;
        transform: translateX(-50%);
      }

      .laptop .hinge {
        position: absolute;
        left: 50%;
        height: 7px;
        border-radius: 0 0 8px 8px;
        background: linear-gradient(180deg, #0a0c0f, #3f454e);
        transform: translateX(-50%);
      }

      .laptop .base {
        position: absolute;
        left: 50%;
        height: 42px;
        clip-path: polygon(5% 0, 95% 0, 100% 72%, 98% 88%, 2% 88%, 0 72%);
        border-radius: 3px 3px 18px 18px;
        background: linear-gradient(180deg, #a8adb4 0%, #727881 17%, #3e444c 48%, #1b1f24 82%);
        box-shadow:
          0 16px 24px rgb(0 0 0 / 24%),
          inset 0 1px 0 rgb(255 255 255 / 45%);
        transform: translateX(-50%);
      }

      .laptop .base::after {
        position: absolute;
        top: 0;
        left: 50%;
        width: 112px;
        height: 7px;
        border-radius: 0 0 8px 8px;
        background: #4a5058;
        content: "";
        transform: translateX(-50%);
      }

      body[data-frame-style="phone"] .laptop,
      body[data-frame-style="laptop"] .phone {
        display: none;
      }
    </style>
  </head>
  <body data-frame-style="__FRAME_STYLE__">
    <div class="hardware phone">
      <div class="screen"><img alt="Captured page"></div>
      <span class="speaker"></span>
      <span class="camera"></span>
      <span class="button button-power"></span>
      <span class="button button-volume-up"></span>
      <span class="button button-volume-down"></span>
    </div>
    <div class="hardware laptop">
      <div class="lid">
        <div class="screen"><img alt="Captured page"></div>
        <span class="camera"></span>
      </div>
      <span class="hinge"></span>
      <span class="base"></span>
    </div>
    <script>
      const style = document.body.dataset.frameStyle;
      const hardware = document.querySelector(`.${style}`);
      const image = hardware.querySelector("img");
      const screen = hardware.querySelector(".screen");

      function sizeHardware(device) {
        const viewportWidth = device.viewport_width;
        const viewportHeight = device.viewport_height;
        let naturalWidth;
        let naturalHeight;

        image.style.width = `${viewportWidth}px`;
        image.style.height = `${viewportHeight}px`;
        screen.style.width = `${viewportWidth}px`;
        screen.style.height = `${viewportHeight}px`;

        if (style === "phone") {
          naturalWidth = viewportWidth + 28;
          naturalHeight = viewportHeight + 58;
          hardware.style.width = `${naturalWidth}px`;
          hardware.style.height = `${naturalHeight}px`;
        } else {
          const lid = hardware.querySelector(".lid");
          const hinge = hardware.querySelector(".hinge");
          const base = hardware.querySelector(".base");
          const lidWidth = viewportWidth + 28;
          const lidHeight = viewportHeight + 28;
          naturalWidth = viewportWidth + 116;
          naturalHeight = viewportHeight + 70;
          hardware.style.width = `${naturalWidth}px`;
          hardware.style.height = `${naturalHeight}px`;
          lid.style.width = `${lidWidth}px`;
          lid.style.height = `${lidHeight}px`;
          hinge.style.top = `${lidHeight - 2}px`;
          hinge.style.width = `${Math.min(220, viewportWidth * 0.28)}px`;
          base.style.top = `${lidHeight + 3}px`;
          base.style.width = `${naturalWidth}px`;
        }

        const scale = Math.min(
          (device.frame_width - 20) / naturalWidth,
          (device.frame_height - 20) / naturalHeight,
          1,
        );
        hardware.style.transform = `scale(${Math.max(scale, 0.05)})`;
      }

      visetFrame.subscribe(async state => {
        document.documentElement.removeAttribute("data-frame-ready");
        sizeHardware(state.device);
        image.src = state.image_url;
        await image.decode();
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        document.documentElement.setAttribute("data-frame-ready", "");
      });
    </script>
  </body>
</html>
"""

    let resolveStyle style (device: Device) =
        match style with
        | Automatic -> if device.Mobile then Phone else Laptop
        | Phone -> Phone
        | Laptop -> Laptop

    let deriveDimensions style (device: Device) =
        let resolved = resolveStyle style device

        let widthChrome, heightChrome =
            match resolved with
            | Phone -> 72L, 112L
            | Laptop -> 128L, 120L
            | Automatic -> invalidOp "Automatic frame style was not resolved."

        let width = int64 device.Viewport.Width + widthChrome
        let height = int64 device.Viewport.Height + heightChrome

        if width > int64 Int32.MaxValue || height > int64 Int32.MaxValue then
            Error(String.Concat("Built-in ", resolved.ToString(), " frame dimensions exceed the supported maximum."))
        else
            Ok
                { Width = int width
                  Height = int height }

    let html style (device: Device) =
        let resolved = resolveStyle style device

        source.Replace("__FRAME_STYLE__", resolved.ToString(), StringComparison.Ordinal)
