--[[
# viset
version = 1
output_root = "output"
output = "animations/{device}-{theme}-home-scroll.webp"
frame = "builtin:auto"
frames_per_second = 30
browser_arguments = ["--hide-scrollbars"]

[webp]
source = "jpeg_screencast"
source_quality = 95
encoder = "libwebp_full"
pipeline = "spooled"
mode = "lossy"
quality = 75
method = 0

[devices.laptop]
mobile = false
touch = false
device_scale = 1.0

[devices.laptop.viewport]
width = 1472
height = 780

[matrix]
theme = ["light"]
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
		viset.script.directory .. "/../../../../examples/medium/site",
	},
})

local succeeded, failure = pcall(function()
	local theme = viset.context.axes.theme
	local device = viset.context.device
	local render = viset.javascript([=[
      async ({ theme, device }) => {
        document.documentElement.dataset.theme = theme;
        document.documentElement.dataset.device = device;
        window.scrollTo(0, 0);
        document.querySelector(".touch-indicator").style.opacity = "0";
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        return true;
      }
    ]=])

	local gesture_factory = viset.javascript([=[
      ({ startRatio, endRatio, touch }) => frame => {
        const root = document.documentElement;
        const maximum = Math.max(0, root.scrollHeight - window.innerHeight);
        const ratio = startRatio + (endRatio - startRatio) * frame.progress;
        window.scrollTo(0, Math.round(maximum * ratio));

        if (touch) {
          const indicator = document.querySelector(".touch-indicator");
          indicator.style.left = Math.round(window.innerWidth * 0.78) + "px";
          indicator.style.top = Math.round(window.innerHeight * (0.78 + (0.42 - 0.78) * frame.progress)) + "px";
          indicator.style.opacity = "1";
        }
      }
    ]=])

	viset.http.wait({ url = url, timeout = "10s" })
	viset.page.navigate(url)
	viset.page.wait_for(viset.javascript("document.readyState === 'complete'"), "10s")
	viset.page.evaluate(render, { theme = theme, device = device.name })

	local recording = viset.record()
	recording:start()
	recording:during("800ms")

	local function capture_gesture(start_ratio, end_ratio)
		local update = string.format(
			"(%s)({startRatio:%s,endRatio:%s,touch:%s})",
			gesture_factory,
			start_ratio,
			end_ratio,
			tostring(device.touch)
		)

		recording:during("700ms", function()
			viset.page.animate({
				duration = "700ms",
				easing = "in_out_sine",
				update = update,
			})
		end)
		viset.page.evaluate(viset.javascript([=[
      document.querySelector(".touch-indicator").style.opacity = "0"
    ]=]))
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
