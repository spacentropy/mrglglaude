#!/usr/bin/env dotnet
// Publishes Claude Code session status to the ClaudeForever addon. Only reads ~/.claude.
// Build: dotnet publish ClaudeForeverBridge.cs -o bin   (NativeAOT)
// Run:   bin/ClaudeForeverBridge [<path to Interface/AddOns>]
// Self-check: bin/ClaudeForeverBridge --selftest
#:property TargetFramework=net11.0
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property UseSystemResourceKeys=true
#:property StackTraceSupport=false
#:property ConcurrentGarbageCollection=false
using System.Diagnostics;
using System.Text;
using System.Text.Json;

const string DefaultAddOns = "/Applications/World of Warcraft/_forever_/Interface/AddOns";
const int Slots = 1000, PeriodSeconds = 10, TickMilliseconds = 2000, IdleTickMilliseconds = 10000, MaxText = 120; // Slots and PeriodSeconds must match ClaudeForever.lua

var claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
var transcripts = new Dictionary<string, TranscriptTail>();
var listings = new Dictionary<string, (DateTime Modified, string[] Entries)>();
var sessionFiles = new Dictionary<string, SessionFile>();

if (args.Contains("--selftest"))
{
    SelfTest();
    Console.WriteLine("ok");
    return 0;
}

var addons = args.FirstOrDefault() ?? DefaultAddOns;
if (!File.Exists(Path.Combine(addons, "ClaudeForever", "ClaudeForever.toc")))
{
    Console.Error.WriteLine($"Copy the ClaudeForever folder into {addons} first, or pass your AddOns path.");
    return 1;
}

if (CreateMissingSlots() > 0)
    Console.WriteLine($"Created slot addons in {addons}. Fully quit and relaunch WoW once so it sees them.");
Console.WriteLine("Bridge started.");

string? published = null;
var publishedSlot = 0;
var gamePid = 0;
bool? gameWasRunning = null;
while (true)
{
    var gameRunning = IsGameRunning();
    if (gameRunning != gameWasRunning)
        Console.WriteLine(gameRunning ? "World of Warcraft is running: publishing." : "World of Warcraft is not running: idle.");
    gameWasRunning = gameRunning;
    // The first publish happens even without the game, so a missing write permission shows up at startup.
    if (!gameRunning && published is not null)
    {
        Thread.Sleep(IdleTickMilliseconds);
        continue;
    }
    try
    {
        var rows = RenderRows(ReadSessions());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var slot = SlotAt(now);
        if (rows != published || slot != publishedSlot)
        {
            // The game loads the slot for the current period; the next one is written early so it is ready at the boundary.
            var feed = $"ClaudeForeverFeed={{now={now},sessions={{{rows}}}}}\n";
            WriteSlot(slot, feed);
            WriteSlot(slot % Slots + 1, feed);
            (published, publishedSlot) = (rows, slot);
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Will retry: {e.Message}");
    }
    Thread.Sleep(TickMilliseconds);
}

static int SlotAt(long unixSeconds) => (int)(unixSeconds / PeriodSeconds % Slots) + 1;

// shortcut: any running WoW flavour counts (retail too); match the executable path if that ever matters.
bool IsGameRunning()
{
    if (gamePid > 0 && IsAlive(gamePid))
        return true;
    var games = Process.GetProcessesByName("World of Warcraft");
    gamePid = games.Length > 0 ? games[0].Id : 0;
    foreach (var game in games)
        game.Dispose();
    return gamePid > 0;
}

static JsonElement Get(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

static string? Text(JsonElement element, string name) =>
    Get(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

static bool Flag(JsonElement element, string name) => Get(element, name).ValueKind == JsonValueKind.True;

// Session text is untrusted and the game executes this file; "|" would otherwise start a WoW UI escape sequence.
static string LuaString(string value)
{
    if (value.Length > MaxText)
        value = value[..(char.IsHighSurrogate(value[MaxText - 1]) ? MaxText - 1 : MaxText)];
    var lua = new StringBuilder("\"");
    foreach (var c in value.Replace("|", "||"))
    {
        if (c is '\\' or '"')
            lua.Append('\\').Append(c);
        else
            lua.Append(char.IsControl(c) ? ' ' : c); // a newline would spill into the next row
    }
    return lua.Append('"').ToString();
}

static string Describe(JsonElement tool)
{
    var input = Get(tool, "input");
    // Never show commands, search patterns or queries: they can hold secrets and the game screen may be shared.
    var detail = Text(input, "description")
        ?? (Text(input, "file_path") is { } path ? Path.GetFileName(path) : null)
        ?? Text(input, "skill");
    var name = (Text(tool, "name") ?? "?").Split("__")[^1];
    return detail is null ? name : $"{name}: {detail}";
}

static bool IsPrompt(JsonElement entry)
{
    if (Text(entry, "type") != "user" || Flag(entry, "isMeta") || Flag(entry, "isCompactSummary"))
        return false;
    var content = Get(Get(entry, "message"), "content");
    return content.ValueKind == JsonValueKind.String
        || (content.ValueKind == JsonValueKind.Array && !content.EnumerateArray().Any(block => Text(block, "type") == "tool_result"));
}

static Transcript ApplyLine(Transcript state, ReadOnlyMemory<byte> line)
{
    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        return state;
    }
    using (document)
    {
        var entry = document.RootElement;
        state = state with { Title = Text(entry, "customTitle") ?? Text(entry, "aiTitle") ?? state.Title };
        if (IsPrompt(entry))
        {
            state = state with { Actions = 0, Activity = "" };
        }
        else if (Text(entry, "type") == "assistant" && Get(Get(entry, "message"), "content") is { ValueKind: JsonValueKind.Array } content)
        {
            foreach (var block in content.EnumerateArray().Where(block => Text(block, "type") == "tool_use"))
                state = state with { Actions = state.Actions + 1, Activity = Describe(block) };
        }
        return state;
    }
}

// Transcripts are append-only, so each read continues from where the previous one stopped.
static TranscriptTail ReadTranscript(TranscriptTail tail)
{
    if (new FileInfo(tail.Path).Length == tail.Offset)
        return tail; // nothing appended: a stat is several times cheaper than opening the file
    using var stream = File.OpenRead(tail.Path);
    var length = stream.Length;
    if (length < tail.Offset)
        tail = new TranscriptTail(tail.Path, 0, Transcript.Empty);
    stream.Seek(tail.Offset, SeekOrigin.Begin);
    // Read in pieces: one array for a transcript of many megabytes stays in the process's memory for good.
    var bytes = new byte[Math.Min(length - tail.Offset, 1 << 16)];
    var (offset, state, filled) = (tail.Offset, tail.State, 0);
    for (int read; (read = stream.Read(bytes, filled, bytes.Length - filled)) > 0;)
    {
        filled += read;
        var end = Array.LastIndexOf(bytes, (byte)'\n', filled - 1) + 1; // a line still being written is read next time
        for (var start = 0; start < end;)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            state = ApplyLine(state, bytes.AsMemory(start, newline - start));
            start = newline + 1;
        }
        (offset, filled) = (offset + end, filled - end);
        Array.Copy(bytes, end, bytes, 0, filled);
        if (filled == bytes.Length)
            Array.Resize(ref bytes, filled * 2); // a line longer than the buffer
    }
    return tail with { Offset = offset, State = state };
}

// Every tick lists the same directories and reads the same session files, and opening either costs several stats,
// so both are reread only when their mtime moves (for a directory: an entry was added or removed).
// shortcut: trusts mtime (nanoseconds on APFS); a filesystem with coarse timestamps can hide a change until the next one, so reread every tick there.
string[] Entries(string name, string pattern)
{
    var directory = Path.Combine(claude, name);
    var modified = Directory.GetLastWriteTimeUtc(directory);
    if (!listings.TryGetValue(directory, out var listing) || listing.Modified != modified)
        listings[directory] = listing = (modified, Directory.GetFileSystemEntries(directory, pattern));
    return listing.Entries;
}

Transcript TranscriptState(string sessionId)
{
    if (!transcripts.TryGetValue(sessionId, out var tail) || !File.Exists(tail.Path))
    {
        var file = sessionId + ".jsonl";
        var path = Entries("projects", "*") // a session with no transcript yet is looked up on every tick
            .Select(project => Path.Combine(project, file))
            .FirstOrDefault(File.Exists);
        if (path is null)
            return Transcript.Empty;
        tail = new TranscriptTail(path, 0, Transcript.Empty);
    }
    transcripts[sessionId] = tail = ReadTranscript(tail);
    return tail.State;
}

static bool IsAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return true;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

List<Session> ReadSessions()
{
    var sessions = new List<Session>();
    foreach (var path in Entries("sessions", "*.json"))
    {
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (!sessionFiles.TryGetValue(path, out var file) || file.Modified != modified)
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var session = document.RootElement;
                var changed = Get(session, "statusUpdatedAt") is { ValueKind: JsonValueKind.Number } updated ? updated : Get(session, "startedAt");
                sessionFiles[path] = file = new SessionFile(modified, Get(session, "pid").GetInt32(), Text(session, "sessionId") ?? throw new InvalidOperationException(),
                    Text(session, "name") ?? "?", Text(session, "status") ?? "unknown", changed.GetInt64() / 1000);
            }
            if (!IsAlive(file.Pid))
                continue;
            var transcript = TranscriptState(file.SessionId);
            sessions.Add(new Session(file.Name, transcript.Title, file.Status, transcript.Activity, transcript.Actions, file.Since));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // the session is exiting or a file is mid-rewrite; the next tick picks it up
        }
    }
    return sessions.OrderBy(s => s.Status == "idle").ThenByDescending(s => s.Since).ToList();
}

static string RenderRows(IEnumerable<Session> sessions) => string.Join(",", sessions.Select(s =>
    $"{{name={LuaString(s.Name)},title={LuaString(s.Title)},status={LuaString(s.Status)},activity={LuaString(s.Activity)},actions={s.Actions},since={s.Since}}}"));

string SlotDirectory(int index) => Path.Combine(addons, $"ClaudeForever_S{index:D3}");

int CreateMissingSlots()
{
    var created = 0;
    for (var index = 1; index <= Slots; index++)
    {
        var directory = SlotDirectory(index);
        if (File.Exists(Path.Combine(directory, "Feed.lua")))
            continue;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Path.GetFileName(directory) + ".toc"),
            $"## Interface: 16001\n## Title: Claude Forever slot {index:D3}\n## Notes: Data slot for Claude Forever. Leave it enabled.\n## Category: Claude Forever slots\n## LoadOnDemand: 1\n\nFeed.lua\n");
        File.WriteAllText(Path.Combine(directory, "Feed.lua"), "");
        created++;
    }
    return created;
}

void WriteSlot(int index, string feed)
{
    var target = Path.Combine(SlotDirectory(index), "Feed.lua");
    var partial = $"{target}.{Environment.ProcessId}.tmp"; // a second bridge instance must not share it
    File.WriteAllText(partial, feed);
    File.Move(partial, target, overwrite: true);
}

void SelfTest()
{
    static void Check(bool condition, string what)
    {
        if (!condition)
            throw new Exception("selftest failed: " + what);
    }
    static string Entry(string type, string content, string extra = "") =>
        $$"""{"type":"{{type}}","message":{"content":{{content}}}{{extra}}}""";
    static string Tool(string name, string input) => $$"""[{"type":"tool_use","name":"{{name}}","input":{{input}}}]""";

    var path = Path.GetTempFileName();
    try
    {
        File.WriteAllLines(path, [
            """{"type":"ai-title","aiTitle":"Fix login"}""",
            Entry("user", "\"first\""),
            Entry("assistant", Tool("Read", """{"file_path":"/a/old.cs"}""")),
            Entry("user", "\"second\""),
            Entry("assistant", Tool("Edit", """{"file_path":"/a/b/new.cs"}""")),
            Entry("user", """[{"type":"tool_result","content":"done"}]"""),
            Entry("user", "\"reminder\"", ""","isMeta":true"""),
            Entry("assistant", Tool("mcp__x__Bash", """{"command":"curl -H SECRET"}""")),
            """{"truncated""",
        ]);
        var tail = ReadTranscript(new TranscriptTail(path, 0, Transcript.Empty));
        Check(tail.State.Title == "Fix login", "title");
        Check(tail.State.Actions == 2, "actions reset on a real prompt only");
        Check(tail.State.Activity == "Bash", "Bash command text stays hidden");
        Check(ReferenceEquals(ReadTranscript(tail), tail), "an unchanged transcript is not reread");

        File.AppendAllText(path, """{"type":"user","message":{"content":"thi""");
        var partial = ReadTranscript(tail);
        Check(partial.Offset == tail.Offset && partial.State.Actions == 2, "a line still being written is left for later");
        File.AppendAllText(path, "rd\"}}\n");
        var resumed = ReadTranscript(partial);
        Check(resumed.State is { Actions: 0, Title: "Fix login" }, "reading continues from the offset");
        File.AppendAllText(path, Entry("assistant", Tool("Edit", $$"""{"description":"{{new string('x', 200_000)}}"}""")) + "\n" + Entry("assistant", Tool("Read", "{}")) + "\n");
        Check(ReadTranscript(resumed).State is { Actions: 2, Activity: "Read" }, "a line longer than the read buffer is kept whole");
    }
    finally
    {
        File.Delete(path);
    }

    // ReadSessions against a scratch ~/.claude: both mtime caches must notice a change made right after a read.
    claude = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var projects = Directory.CreateDirectory(Path.Combine(claude, "projects")).FullName;
        var session = Path.Combine(Directory.CreateDirectory(Path.Combine(claude, "sessions")).FullName, "1.json");
        string Rows(string status)
        {
            File.WriteAllText(session, $$"""{"pid":{{Environment.ProcessId}},"sessionId":"s","status":"{{status}}","startedAt":0}""");
            return RenderRows(ReadSessions());
        }
        Check(Rows("busy").Contains("status=\"busy\",activity=\"\""), "a session without a transcript is listed");
        File.WriteAllLines(Path.Combine(Directory.CreateDirectory(Path.Combine(projects, "p")).FullName, "s.jsonl"), [Entry("assistant", Tool("Read", "{}"))]);
        Check(Rows("idle").Contains("status=\"idle\",activity=\"Read\""), "a rewritten session file and a new project are picked up");
    }
    finally
    {
        Directory.Delete(claude, recursive: true);
    }

    Check(SlotAt(0) == 1 && SlotAt(PeriodSeconds - 1) == 1 && SlotAt(PeriodSeconds) == 2, "slot follows the period");
    Check(SlotAt((long)PeriodSeconds * Slots - 1) == Slots && SlotAt((long)PeriodSeconds * Slots) == 1, "slot wraps");
    Check(LuaString("a\"\\\n|c\x7f") == "\"a\\\"\\\\ ||c \"", "escaping");
    var cut = LuaString(new string('x', MaxText - 1) + "😀");
    Check(cut == "\"" + new string('x', MaxText - 1) + "\"", "truncation keeps surrogate pairs whole");
}

record Transcript(string Title, int Actions, string Activity)
{
    public static readonly Transcript Empty = new("", 0, "");
}
record TranscriptTail(string Path, long Offset, Transcript State);
record SessionFile(DateTime Modified, int Pid, string SessionId, string Name, string Status, long Since);
record Session(string Name, string Title, string Status, string Activity, int Actions, long Since);
