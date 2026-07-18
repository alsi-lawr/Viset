--[[
# viset
version = 1
output = "animations/live-spill.webp"
frame = "frame.html"
frames_per_second = 60
browser_arguments = []

[webp]
source = "jpeg_screencast"
source_quality = 95
pipeline = "live"

[devices.fixture]
mobile = false
touch = true
device_scale = 1.0

[devices.fixture.viewport]
width = 1600
height = 900

[devices.fixture.frame]
width = 1600
height = 900

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

if os.getenv("VISET_FIXTURE_FAIL") == "1" then
  error("forced fixture failure")
end

viset.http.wait({ url = url, timeout = "10s" })
viset.page.navigate(url)
viset.page.wait_for("window.fixture !== undefined", "10s")
local set_motion = viset.javascript [=[
  ({ progress }) => {
    window.fixture.setView("motion", progress);
    return true;
  }
]=]
viset.page.evaluate(set_motion, { progress = 0 })

local recording = viset.record()
recording:start()
recording:during("200ms", function()
  recording:during("160ms", function()
    viset.page.animate({
      duration = "160ms",
      easing = "in_sine",
      update = viset.javascript [=[
        frame => window.fixture.setView("motion", frame.progress * 0.45)
      ]=],
    })
  end)
end)
recording:stop()

viset.page.evaluate(set_motion, { progress = 0.75 })
viset.sleep("1s")

recording:start()
recording:during("200ms", function()
  viset.page.animate({
    duration = "160ms",
    easing = "out_sine",
    update = viset.javascript [=[
      frame => window.fixture.setView("motion", 0.55 + frame.progress * 0.45)
    ]=],
  })
end)
recording:stop()
viset.process.stop(server)
