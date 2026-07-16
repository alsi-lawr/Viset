--[[
# viset
version = 1
output_root = "output"
output = "animations/{device}-{theme}-home-scroll.webp"
frame = "builtin:auto"
frames_per_second = 30
browser_arguments = ["--hide-scrollbars"]

[devices.laptop]
mobile = false
touch = false
device_scale = 1.0

[devices.laptop.viewport]
width = 1180
height = 720

[devices.phone]
mobile = true
touch = true
device_scale = 1.0

[devices.phone.viewport]
width = 390
height = 844

[matrix]
device = ["laptop", "phone"]
theme = ["light", "dark"]
]]

local python = os.getenv("VISET_EXAMPLE_PYTHON") or "python3"
local port = os.getenv("VISET_EXAMPLE_PORT") or "41736"
local url = "http://127.0.0.1:" .. port .. "/"
local server = viset.process.start({
  file = python,
  arguments = {
    "-m",
    "http.server",
    port,
    "--bind",
    "127.0.0.1",
    "--directory",
    viset.script.directory .. "/site",
  },
})

local succeeded, failure = pcall(function()
  local theme = viset.context.axes.theme
  local device = viset.context.device
  local quoted_theme = string.format("%q", theme)
  local quoted_device = string.format("%q", device.name)

  viset.http.wait({ url = url, timeout = "10s" })
  viset.page.navigate(url)
  viset.page.wait_for("window.blokeBot !== undefined", "10s")
  viset.page.evaluate(string.format(
    "(async()=>{window.blokeBot.render(%s,%s);await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));return true})()",
    quoted_theme,
    quoted_device
  ))

  local recording = viset.record()
  recording:start()
  recording:during("800ms")

  local function capture_gesture(start_ratio, end_ratio)
    recording:during("700ms", function()
      viset.page.animate({
        duration = "700ms",
        easing = "in_out_sine",
        update = string.format(
          "frame=>window.blokeBot.gesture(%s,%s,frame.progress)",
          start_ratio,
          end_ratio
        ),
      })
    end)
    viset.page.evaluate("window.blokeBot.touch(false)")
  end

  capture_gesture(0, 0.48)
  recording:during("250ms")
  capture_gesture(0.48, 1)
  recording:during("500ms")
  recording:stop()
end)

viset.process.stop(server)

if not succeeded then
  error(failure, 0)
end
