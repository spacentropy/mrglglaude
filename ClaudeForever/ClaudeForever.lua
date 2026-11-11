local SLOTS, ROW_HEIGHT, HEADER_HEIGHT, FOOTER_HEIGHT = 1000, 36, 30, 20
local PERIOD, OFFLINE_AFTER = 10, 60 -- PERIOD and SLOTS must match ClaudeForeverBridge.cs
local COLORS = { busy = { 0.25, 0.85, 0.35 }, idle = { 0.5, 0.5, 0.5 } }
local OTHER_STATUS = { 1, 0.75, 0.1 }

local rows = {}
local lastPeriod, problem, exhausted

local frame = CreateFrame("Frame", "ClaudeForeverFrame", UIParent, "BackdropTemplate")
-- Size and position are saved by the client's layout cache because the frame is named and user-placed.
frame:SetSize(380, HEADER_HEIGHT + 6 * ROW_HEIGHT + FOOTER_HEIGHT)
frame:SetPoint("TOPRIGHT", -220, -180)
frame:SetResizable(true)
frame:SetResizeBounds(240, HEADER_HEIGHT + ROW_HEIGHT + FOOTER_HEIGHT, 900, 900)
frame:SetBackdrop({
	bgFile = "Interface\\Tooltips\\UI-Tooltip-Background",
	edgeFile = "Interface\\Tooltips\\UI-Tooltip-Border",
	tile = true, tileSize = 16, edgeSize = 16,
	insets = { left = 4, right = 4, top = 4, bottom = 4 },
})
frame:SetBackdropColor(0, 0, 0, 0.85)
frame:SetClampedToScreen(true)
frame:SetMovable(true)
frame:EnableMouse(true)
frame:RegisterForDrag("LeftButton")
frame:SetScript("OnDragStart", frame.StartMoving)
frame:SetScript("OnDragStop", frame.StopMovingOrSizing)

local heading = frame:CreateFontString(nil, "OVERLAY", "GameFontNormal")
heading:SetPoint("TOPLEFT", 12, -10)
heading:SetText("Claude")

local summary = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
summary:SetPoint("LEFT", heading, "RIGHT", 8, 0)
summary:SetPoint("RIGHT", frame, "TOPRIGHT", -30, -16)
summary:SetJustifyH("LEFT")
summary:SetWordWrap(false)

local close = CreateFrame("Button", nil, frame, "UIPanelCloseButton")
close:SetPoint("TOPRIGHT", 0, 0)

local footer = frame:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
footer:SetPoint("BOTTOMLEFT", 12, 8)

local grip = CreateFrame("Button", nil, frame)
grip:SetSize(16, 16)
grip:SetPoint("BOTTOMRIGHT", -4, 4)
grip:SetNormalTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Up")
grip:SetHighlightTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Highlight")
grip:SetPushedTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Down")
grip:SetScript("OnMouseDown", function() frame:StartSizing("BOTTOMRIGHT") end)
grip:SetScript("OnMouseUp", function() frame:StopMovingOrSizing() end)

local function getRow(i)
	if rows[i] then return rows[i] end
	local row = CreateFrame("Frame", nil, frame)
	local top = -HEADER_HEIGHT - (i - 1) * ROW_HEIGHT
	row:SetHeight(ROW_HEIGHT)
	row:SetPoint("TOPLEFT", 12, top)
	row:SetPoint("TOPRIGHT", -12, top)

	row.dot = row:CreateTexture(nil, "ARTWORK")
	row.dot:SetSize(8, 8)
	row.dot:SetPoint("TOPLEFT", 0, -3)

	row.meta = row:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
	row.meta:SetPoint("TOPRIGHT", 0, -1)

	row.title = row:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
	row.title:SetPoint("TOPLEFT", 14, 0)
	row.title:SetPoint("RIGHT", row.meta, "LEFT", -8, 0)
	row.title:SetJustifyH("LEFT")
	row.title:SetWordWrap(false)

	row.detail = row:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
	row.detail:SetPoint("TOPLEFT", 14, -15)
	row.detail:SetPoint("RIGHT", row, "RIGHT", 0, 0)
	row.detail:SetJustifyH("LEFT")
	row.detail:SetWordWrap(false)

	rows[i] = row
	return row
end

local function ago(seconds)
	seconds = math.max(0, seconds)
	if seconds < 60 then return seconds .. "s" end
	if seconds < 3600 then return math.floor(seconds / 60) .. "m" end
	return math.floor(seconds / 3600) .. "h"
end

local function countBusy(sessions)
	local busy = 0
	for _, session in ipairs(sessions) do
		if session.status ~= "idle" then busy = busy + 1 end
	end
	return busy
end

local function headline(sessions)
	if problem then return "Slot load failed (" .. problem .. "): run the bridge, restart WoW" end
	if not ClaudeForeverFeed then return "No data yet: is the bridge running?" end
	if exhausted then return "Out of live updates: /reload" end
	local age = time() - ClaudeForeverFeed.now
	if age > OFFLINE_AFTER then return "Bridge offline, data is " .. ago(age) .. " old" end
	return countBusy(sessions) .. " working, " .. #sessions .. " open"
end

local function render()
	local sessions = ClaudeForeverFeed and ClaudeForeverFeed.sessions or {}
	local now = time()
	local fits = math.floor((frame:GetHeight() - HEADER_HEIGHT - FOOTER_HEIGHT) / ROW_HEIGHT)
	local shown = math.min(#sessions, math.max(1, fits))

	summary:SetText(headline(sessions))
	for i = 1, shown do
		local session, row = sessions[i], getRow(i)
		local duration = ago(now - session.since)
		row.dot:SetColorTexture(unpack(COLORS[session.status] or OTHER_STATUS))
		row.title:SetText(session.title ~= "" and session.title or session.name)
		if session.status == "busy" then
			row.meta:SetText(session.actions .. " actions, " .. duration)
			row.detail:SetText(session.activity ~= "" and session.name .. ": " .. session.activity or session.name)
		else
			row.meta:SetText(session.status .. " " .. duration)
			row.detail:SetText(session.name)
		end
		row:Show()
	end
	for i = shown + 1, #rows do rows[i]:Hide() end

	local hidden = #sessions - shown
	footer:SetText(hidden > 0 and "+" .. hidden .. " more" or "")
end

-- A load-on-demand addon's Lua is read from disk when loaded, but only once per UI session.
-- Both sides derive the slot from the shared clock, so the bridge only has to write this slot and the next.
local function poll()
	local period = math.floor(time() / PERIOD)
	if period == lastPeriod then return end
	lastPeriod = period
	local name = string.format("ClaudeForever_S%03d", period % SLOTS + 1)
	exhausted = C_AddOns.IsAddOnLoaded(name)
	if exhausted then return end
	local previous = ClaudeForeverFeed
	local loaded, reason = C_AddOns.LoadAddOn(name)
	problem = not loaded and reason or nil
	-- A slot the bridge didn't write this cycle (it was stopped) holds older data; keep the newer one.
	if previous and ClaudeForeverFeed and ClaudeForeverFeed.now < previous.now then ClaudeForeverFeed = previous end
end

local function refresh()
	poll()
	render()
end

frame:SetScript("OnShow", refresh)
frame:SetScript("OnSizeChanged", render)
frame:RegisterEvent("PLAYER_LOGIN")
frame:SetScript("OnEvent", function()
	-- The saved layout may predate resizing, when the panel could be one header tall.
	frame:SetHeight(math.max(frame:GetHeight(), HEADER_HEIGHT + ROW_HEIGHT + FOOTER_HEIGHT))
	refresh()
	C_Timer.NewTicker(1, function()
		if frame:IsShown() then refresh() end
	end)
end)

SLASH_CLAUDEFOREVER1 = "/claude"
SlashCmdList.CLAUDEFOREVER = function()
	frame:SetShown(not frame:IsShown())
end
