-- Revocation, global limit and route limit in one atomic call (LLD 07 §6.2).
-- KEYS, in this order and only when used: rev:{sub}, fw:global:{ip}:{n}, the route policy's key.
-- ARGV: now_ms, iat_ms or '', request_id, global_limit (0 = skip), global_window_ms,
--       route algorithm ('fixed' | 'sliding' | 'bucket' | '' = none), route policy name, limit|burst, window_ms|per_minute.
-- Returns {revoked, allowed, retry_after_ms, denied_policy}.
local now = tonumber(ARGV[1])
local k = 0

-- Returns 0 when allowed, else the ms until the next window. Denied requests are counted too.
local function fixed(key, limit, window)
  local count = redis.call('INCR', key)
  if count == 1 then redis.call('PEXPIRE', key, window + 5000) end
  if count > limit then return window - now % window end
  return 0
end

if ARGV[2] ~= '' then
  k = k + 1
  local rev = tonumber(redis.call('GET', KEYS[k]))
  if rev and tonumber(ARGV[2]) < rev then return {1, 0, 0, ''} end
end

local global_limit = tonumber(ARGV[4])
if global_limit > 0 then
  k = k + 1
  local retry = fixed(KEYS[k], global_limit, tonumber(ARGV[5]))
  if retry > 0 then return {0, 0, retry, 'global'} end
end

local algorithm = ARGV[6]
if algorithm ~= '' then
  k = k + 1
  local key, first, second = KEYS[k], tonumber(ARGV[8]), tonumber(ARGV[9])
  local retry = 0
  if algorithm == 'fixed' then
    retry = fixed(key, first, second)
  elseif algorithm == 'sliding' then
    local window = second
    redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window) -- an entry exactly one window old is out
    if redis.call('ZCARD', key) < first then
      redis.call('ZADD', key, now, now .. ':' .. ARGV[3])
      redis.call('PEXPIRE', key, window)
    else
      retry = tonumber(redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')[2]) + window - now
    end
  else
    local rate = second / 60000 -- tokens per ms
    local state = redis.call('HMGET', key, 'tokens', 'ts')
    local tokens, ts = tonumber(state[1]), tonumber(state[2])
    if not tokens then tokens, ts = first, now end
    tokens = math.min(first, tokens + math.max(0, now - ts) * rate)
    if tokens >= 1 - 1e-9 then
      tokens = tokens - 1
    else
      retry = math.ceil((1 - tokens) / rate)
    end
    redis.call('HSET', key, 'tokens', tokens, 'ts', now)
    redis.call('PEXPIRE', key, math.ceil(2 * first / rate))
  end
  if retry > 0 then return {0, 0, retry, ARGV[7]} end
end

return {0, 1, 0, ''}
