--[[
# viset
version = 1
output = "screenshots/{view}.png"
frame = "frame.html"
browser_arguments = []

[devices.fixture]
mobile = false
touch = true
device_scale = 1.0

[devices.fixture.viewport]
width = 240
height = 160

[devices.fixture.frame]
width = 400
height = 300

[matrix]
view = ["red", "blue"]
]]

local python = assert(os.getenv("VISET_PYTHON"), "VISET_PYTHON is required")
local port = assert(os.getenv("VISET_FIXTURE_PORT"), "VISET_FIXTURE_PORT is required")
local root = assert(os.getenv("VISET_FIXTURE_ROOT"), "VISET_FIXTURE_ROOT is required")
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
    root .. "/site",
  },
})

viset.http.wait({ url = url, timeout = "10s" })
viset.page.navigate(url)
viset.page.wait_for("window.fixture !== undefined", "10s")
viset.page.evaluate(viset.javascript [=[
  ({ view }) => {
    window.fixture.setView(view, 0);
    return true;
  }
]=], { view = viset.context.axes.view })
viset.emulation.touch(20, 20)
viset.snapshot()
viset.process.stop(server)
