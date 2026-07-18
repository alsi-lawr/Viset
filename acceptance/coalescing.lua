--[[
# viset
version = 1
output = "animations/coalesced.webp"
frames_per_second = 30

[devices.fixture]

[devices.fixture.viewport]
width = 320
height = 240
]]

viset.page.navigate("data:text/html,<style>html{background:%231f6feb}</style>")
local recording = viset.record()
recording:start()
recording:during("200ms")
recording:stop()
viset.page.evaluate("document.documentElement.style.background = '#d6384a'; true")
recording:start()
recording:during("200ms")
recording:stop()
