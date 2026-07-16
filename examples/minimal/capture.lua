--[[
# viset
version = 1
output_root = "output"
output = "screenshots/home.png"
device = "desktop"
browser_arguments = []

[devices.desktop]
mobile = false
touch = false
device_scale = 1.0

[devices.desktop.viewport]
width = 960
height = 600
]]

local python = os.getenv("VISET_EXAMPLE_PYTHON") or "python3"
local port = os.getenv("VISET_EXAMPLE_PORT") or "41735"
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
  viset.http.wait({ url = url, timeout = "10s" })
  viset.page.navigate(url)
  viset.page.wait_for("window.minimalExampleReady === true", "10s")
  viset.snapshot()
end)

viset.process.stop(server)

if not succeeded then
  error(failure, 0)
end
