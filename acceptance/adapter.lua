local helper = require("helper")
local process_handle = nil

local function fixture_url()
  return "http://127.0.0.1:" .. assert(os.getenv("VISET_FIXTURE_PORT")) .. "/"
end

local function set_view(context, progress)
  local view = context.axes.view or context.data.view
  local script = string.format(
    "window.fixture.setView('%s', %.6f); true",
    view,
    progress or 0
  )
  helper.assert_ok(viset.page.evaluate(script))
end

return {
  prepare = function(context)
    helper.append_log("prepare:" .. context.logical_name)
  end,

  start = function(context)
    local root = assert(os.getenv("VISET_FIXTURE_ROOT"), "VISET_FIXTURE_ROOT is required")
    local python = assert(os.getenv("VISET_PYTHON"), "VISET_PYTHON is required")
    local port = assert(os.getenv("VISET_FIXTURE_PORT"), "VISET_FIXTURE_PORT is required")
    local started = helper.assert_ok(viset.process.start({
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
    }))
    process_handle = started.handle
    helper.append_log("start:" .. context.logical_name .. ":" .. started.process_id)
  end,

  ready = function(context)
    for _ = 1, 50 do
      local response = viset.http.get({ url = fixture_url(), timeout_ms = 200 })
      if response.ok and response.status == 200 then
        helper.append_log("ready:" .. context.logical_name)
        return
      end

      local deadline = os.clock() + 0.02
      while os.clock() < deadline do end
    end

    error("fixture site did not become ready")
  end,

  open = function(context)
    helper.assert_ok(viset.page.navigate(fixture_url()))
    helper.assert_ok(viset.page.wait_for("window.fixture !== undefined", 5000))
    helper.assert_ok(viset.emulation.apply(context.device))
    set_view(context, 0)
    helper.assert_ok(viset.emulation.touch(20, 20))
    helper.append_log("open:" .. context.logical_name)

    if context.kind == "still" then
      helper.assert_ok(viset.capture.still())
    end
  end,

  workflows = {
    motion = function(context)
      for index = 0, 3 do
        set_view(context, index / 3)
        helper.assert_ok(viset.capture.frame())

        if index == 0 and os.getenv("VISET_FIXTURE_FAIL") == "1" then
          error("forced fixture workflow failure")
        end
      end
      helper.append_log("workflow:" .. context.logical_name)
    end,
  },

  stop = function(context)
    if process_handle ~= nil then
      helper.assert_ok(viset.process.stop(process_handle))
      process_handle = nil
    end
    helper.append_log("stop:" .. context.logical_name)
  end,
}
