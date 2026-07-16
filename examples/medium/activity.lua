--[[
# viset
version = 1
output_root = "output"
output = "animations/desktop-{theme}-activity.webp"
device = "desktop"
frame = "builtin:laptop"
frames_per_second = 30
browser_arguments = []

[devices.desktop]
mobile = false
touch = false
device_scale = 1.0

[devices.desktop.viewport]
width = 960
height = 600

[matrix]
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
  viset.page.wait_for("window.dashboard !== undefined", "10s")
  viset.page.evaluate(string.format(
    "(async()=>{window.dashboard.render(%s,%s,0);await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));return true})()",
    quoted_theme,
    quoted_device
  ))

  local recording = viset.record()
  recording:start()
  recording:during("900ms", function()
    viset.page.animate({
      duration = "900ms",
      easing = "in_out_sine",
      update = string.format(
        "frame=>window.dashboard.render(%s,%s,frame.progress)",
        quoted_theme,
        quoted_device
      ),
    })
  end)
  recording:stop()
end)

viset.process.stop(server)

if not succeeded then
  error(failure, 0)
end
