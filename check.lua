for _, domain in ipairs(memory.getmemorydomainlist()) do
  print(domain .. " — " .. memory.getmemorydomainsize(domain) .. " bytes")
end