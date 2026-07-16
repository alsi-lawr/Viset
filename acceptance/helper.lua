local helper = {}

function helper.assert_ok(result)
  if not result.ok then
    error(result.error.code .. ": " .. result.error.message)
  end

  return result
end

function helper.append_log(message)
  local path = assert(os.getenv("VISET_FIXTURE_LOG"), "VISET_FIXTURE_LOG is required")
  local file = assert(io.open(path, "a"))
  file:write(message, "\n")
  file:close()
end

return helper
