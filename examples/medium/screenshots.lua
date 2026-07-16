--[[
# viset
version = 1
output_root = "output"
output = "screenshots/{device}-{theme}.png"
frame = "builtin:auto"
browser_arguments = []

[devices.desktop]
mobile = false
touch = false
device_scale = 1.0

[devices.desktop.viewport]
width = 960
height = 600

[devices.phone]
mobile = true
touch = true
device_scale = 2.0

[devices.phone.viewport]
width = 390
height = 700

[matrix]
device = ["desktop", "phone"]
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
  local render = string.format(
    "(async()=>{window.dashboard.render(%q,%q,0);await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));return true})()",
    theme,
    device.name
  )

  viset.http.wait({ url = url, timeout = "10s" })
  viset.page.navigate(url)
  viset.page.wait_for("window.dashboard !== undefined", "10s")
  viset.page.evaluate(render)

  if device.touch then
    viset.emulation.touch(24, 24)
  end

  viset.snapshot()
end)

viset.process.stop(server)

if not succeeded then
  error(failure, 0)
end
