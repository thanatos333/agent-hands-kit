using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopAgent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_V2);

        if (args.Length >= 1 && args[0] == "serve")
        {
            // Hide our console window immediately, then detach from it:
            // the tray icon is the visible presence, not a console window.
            IntPtr con = NativeMethods.GetConsoleWindow();
            if (con != IntPtr.Zero) NativeMethods.ShowWindow(con, NativeMethods.SW_HIDE);
            NativeMethods.FreeConsole();
            return Server.Run();
        }

        // Client mode: when hosted by a GUI parent there may be no console;
        // attach to the parent's console if we don't have one.
        if (NativeMethods.GetConsoleWindow() == IntPtr.Zero &&
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
        }
        try
        {
            return RunClient(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            return 1;
        }
    }

    private static string ReadToken()
    {
        string p = Server.TokenPath;
        if (!File.Exists(p))
            throw new InvalidOperationException($"token file not found at {p} — is the desktop agent installed/running? (Start-ScheduledTask AronaDesktopAgent)");
        return File.ReadAllText(p).Trim();
    }

    private static async Task<string> SendAsync(JsonObject req)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        http.DefaultRequestHeaders.Add("X-Token", ReadToken());
        var content = new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync($"http://127.0.0.1:{Server.Port}/cmd", content);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"cannot reach the desktop agent server on 127.0.0.1:{Server.Port} ({ex.InnerException?.Message ?? ex.Message}). Start it with: Start-ScheduledTask -TaskName AronaDesktopAgent");
        }
        return await resp.Content.ReadAsStringAsync();
    }

    private sealed class ArgParser
    {
        private readonly Queue<string> _pos = new();
        private readonly Dictionary<string, string> _flags = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _switches = new(StringComparer.OrdinalIgnoreCase);
        public ArgParser(string[] args, int start)
        {
            int i = start;
            while (i < args.Length)
            {
                string a = args[i];
                if (a.StartsWith("--"))
                {
                    string name = a[2..];
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { _flags[name] = args[i + 1]; i += 2; }
                    else { _switches.Add(name); i += 1; }
                }
                else { _pos.Enqueue(a); i++; }
            }
        }
        public string Pos(int idx, string? fallback = null)
        {
            var arr = _pos.ToArray();
            return idx < arr.Length ? arr[idx] : fallback ?? throw new InvalidOperationException($"missing positional argument #{idx}");
        }
        public string Rest(int startIdx)
        {
            var arr = _pos.ToArray();
            if (arr.Length <= startIdx) throw new InvalidOperationException("missing text argument");
            return string.Join(" ", arr[startIdx..]);
        }
        public string? Flag(string name) => _flags.TryGetValue(name, out var v) ? v : null;
        public bool Switch(string name) => _switches.Contains(name);
        public int FlagInt(string name, int fallback) => _flags.TryGetValue(name, out var v) ? int.Parse(v) : fallback;
    }

    private static async Task<int> RunClient(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }
        string cmd = args[0].ToLowerInvariant();
        var p = new ArgParser(args, 1);
        var req = new JsonObject();
        switch (cmd)
        {
            case "ping": req["action"] = "ping"; break;
            case "status": req["action"] = "status"; break;
            case "idle": req["action"] = "idle"; break;
            case "monitors": req["action"] = "monitors"; break;
            case "windows": req["action"] = "windows"; break;
            case "stop-server": req["action"] = "stop-server"; break;
            case "screenshot":
            {
                req["action"] = "screenshot";
                if (p.Flag("window") is string w) req["window"] = w;
                if (p.Flag("out") is string o) req["out"] = o;
                if (p.Flag("format") is string f) req["format"] = f;
                req["quality"] = p.FlagInt("quality", 85);
                if (p.Flag("scale") is string sc) req["scale"] = double.Parse(sc, System.Globalization.CultureInfo.InvariantCulture);
                if (p.Flag("rect") is string r)
                {
                    var parts = r.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 4) throw new InvalidOperationException("--rect needs x,y,w,h");
                    req["rect"] = new JsonArray(parts[0], parts[1], parts[2], parts[3]);
                }
                break;
            }
            case "snapshot":
            {
                req["action"] = "snapshot";
                if (p.Flag("window") is string w) req["window"] = w;
                else if (true) { try { req["window"] = p.Pos(0); } catch { /* foreground default */ } }
                req["depth"] = p.FlagInt("depth", p.Switch("full") ? 14 : 10);
                req["max"] = p.FlagInt("max", p.Switch("full") ? 1000 : 300);
                req["diff"] = p.Switch("diff");
                if (p.Flag("region") is string rg)
                {
                    var parts = rg.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 4) throw new InvalidOperationException("--region needs x,y,w,h");
                    req["region"] = new JsonArray(parts[0], parts[1], parts[2], parts[3]);
                }
                break;
            }
            case "focus":
                req["action"] = "focus";
                req["window"] = p.Pos(0);
                break;
            case "pixel":
                req["action"] = "pixel";
                req["x"] = int.Parse(p.Pos(0));
                req["y"] = int.Parse(p.Pos(1));
                break;
            case "histogram":
            {
                req["action"] = "histogram";
                if (p.Flag("window") is string hw2) req["window"] = hw2;
                if (p.Flag("rect") is string hr)
                {
                    var parts = hr.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 4) throw new InvalidOperationException("--rect needs x,y,w,h");
                    req["rect"] = new JsonArray(parts[0], parts[1], parts[2], parts[3]);
                }
                req["top"] = p.FlagInt("top", 8);
                break;
            }
            case "find-image":
            {
                req["action"] = "find-image";
                string tplPath = p.Flag("file") ?? throw new InvalidOperationException("find-image needs --file <template.png>");
                req["templateBase64"] = Convert.ToBase64String(File.ReadAllBytes(tplPath));
                if (p.Flag("region") is string fr)
                {
                    var parts = fr.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 4) throw new InvalidOperationException("--region needs x,y,w,h");
                    req["rect"] = new JsonArray(parts[0], parts[1], parts[2], parts[3]);
                }
                if (p.Flag("window") is string fw) req["window"] = fw;
                if (p.Flag("threshold") is string th) req["threshold"] = double.Parse(th, System.Globalization.CultureInfo.InvariantCulture);
                break;
            }
            case "find-text":
            {
                req["action"] = "find-text";
                req["text"] = p.Rest(0);
                if (p.Flag("region") is string tr)
                {
                    var parts = tr.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 4) throw new InvalidOperationException("--region needs x,y,w,h");
                    req["rect"] = new JsonArray(parts[0], parts[1], parts[2], parts[3]);
                }
                if (p.Flag("window") is string tw2) req["window"] = tw2;
                if (p.Flag("lang") is string lg) req["lang"] = lg;
                break;
            }
            case "lease":
            {
                string sub = p.Pos(0).ToLowerInvariant();
                if (sub == "acquire")
                {
                    req["action"] = "lease-acquire";
                    req["ttl"] = p.FlagInt("ttl", 300);
                }
                else if (sub == "release")
                {
                    req["action"] = "lease-release";
                    req["force"] = p.Switch("force");
                }
                else if (sub == "status") req["action"] = "lease-status";
                else throw new InvalidOperationException("lease needs 'acquire', 'release' or 'status'");
                break;
            }
            case "click":
            case "double_click":
            case "right_click":
            {
                req["action"] = "click";
                string button = cmd == "right_click" ? "right" : (p.Flag("button") ?? "left");
                int count = cmd == "double_click" ? 2 : p.FlagInt("count", 1);
                req["button"] = button;
                req["count"] = count;
                if (p.Flag("xy") is string xy)
                {
                    var parts = xy.Split(',').Select(int.Parse).ToArray();
                    req["x"] = parts[0]; req["y"] = parts[1];
                }
                else if (p.Flag("ixy") is string ixy)
                {
                    var parts = ixy.Split(',').Select(double.Parse).ToArray();
                    var img = (p.Flag("img") ?? throw new InvalidOperationException("--ixy needs --img WxH")).Split('x').Select(int.Parse).ToArray();
                    req["ix"] = parts[0]; req["iy"] = parts[1];
                    req["imgW"] = img[0]; req["imgH"] = img[1];
                }
                else if (p.Flag("anchor") is string anch && p.Flag("rel") is string rel)
                {
                    var parts = rel.Split(',').Select(int.Parse).ToArray();
                    if (parts.Length != 2) throw new InvalidOperationException("--rel needs dx,dy");
                    req["anchorWindow"] = anch; req["relX"] = parts[0]; req["relY"] = parts[1];
                }
                else req["element"] = p.Pos(0);
                break;
            }
            case "drag":
                req["action"] = "drag";
                req["x1"] = int.Parse(p.Pos(0)); req["y1"] = int.Parse(p.Pos(1));
                req["x2"] = int.Parse(p.Pos(2)); req["y2"] = int.Parse(p.Pos(3));
                if (p.Flag("button") is string b) req["button"] = b;
                break;
            case "scroll":
                req["action"] = "scroll";
                req["x"] = int.Parse(p.Pos(0)); req["y"] = int.Parse(p.Pos(1));
                req["delta"] = p.FlagInt("delta", -360);
                req["horizontal"] = p.Switch("horizontal");
                break;
            case "move":
                req["action"] = "move";
                req["x"] = int.Parse(p.Pos(0)); req["y"] = int.Parse(p.Pos(1));
                req["relative"] = p.Switch("rel");
                break;
            case "path":
            {
                req["action"] = "path";
                var pts = new JsonArray();
                for (int i = 0; ; i++)
                {
                    string tok;
                    try { tok = p.Pos(i); } catch { break; }
                    var xy = tok.Split(',').Select(int.Parse).ToArray();
                    if (xy.Length != 2) throw new InvalidOperationException("path points must be x,y");
                    pts.Add(new JsonArray(xy[0], xy[1]));
                }
                if (pts.Count == 0) throw new InvalidOperationException("path needs at least one x,y point");
                req["points"] = pts;
                req["duration"] = p.FlagInt("duration", 500);
                break;
            }
            case "type":
                req["action"] = "type";
                req["text"] = p.Rest(0);
                break;
            case "key":
                req["action"] = "key";
                req["chord"] = p.Pos(0);
                break;
            case "keydown":
                req["action"] = "keydown";
                req["key"] = p.Pos(0);
                break;
            case "keyup":
                req["action"] = "keyup";
                req["key"] = p.Pos(0);
                break;
            case "hold":
                req["action"] = "hold";
                req["key"] = p.Pos(0);
                req["ms"] = int.Parse(p.Pos(1));
                break;
            case "raw":
            {
                req["action"] = "raw";
                string json = p.Flag("file") is string rf ? File.ReadAllText(rf) : p.Rest(0);
                req["events"] = JsonNode.Parse(json) ?? throw new InvalidOperationException("raw: invalid JSON events");
                break;
            }
            case "sequence":
            {
                req["action"] = "sequence";
                string json = p.Flag("file") is string sf ? File.ReadAllText(sf) : p.Rest(0);
                req["steps"] = JsonNode.Parse(json) ?? throw new InvalidOperationException("sequence: invalid JSON steps");
                if (p.Switch("dry-run")) req["dryRun"] = true;
                if (p.Switch("no-receipt")) req["receipt"] = false;
                break;
            }
            case "setvalue":
                req["action"] = "setvalue";
                req["element"] = p.Pos(0);
                req["text"] = p.Rest(1);
                break;
            case "scrollintoview":
                req["action"] = "scrollintoview";
                req["element"] = p.Pos(0);
                break;
            case "launch":
                req["action"] = "launch";
                req["app"] = p.Pos(0);
                if (p.Flag("args") is string la) req["args"] = la;
                break;
            case "clipboard":
            {
                string sub = p.Pos(0).ToLowerInvariant();
                if (sub == "get") req["action"] = "clipboard-get";
                else if (sub == "set") { req["action"] = "clipboard-set"; req["text"] = p.Rest(1); }
                else throw new InvalidOperationException("clipboard needs 'get' or 'set'");
                break;
            }
            case "paste":
                req["action"] = "paste";
                req["text"] = p.Flag("file") is string pf ? File.ReadAllText(pf) : p.Rest(0);
                break;
            default:
                Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = $"unknown command '{cmd}' (try: help)" }.ToJsonString());
                return 1;
        }

        // Global options for every command: --hwnd binds input to a window
        // (the server refuses when it is not in front), --owner names this
        // client for the focus lease (or set DESKTOP_AGENT_OWNER).
        if (p.Flag("hwnd") is string hwndFlag) req["hwnd"] = long.Parse(hwndFlag);
        string? ownerFlag = p.Flag("owner") ?? Environment.GetEnvironmentVariable("DESKTOP_AGENT_OWNER");
        if (ownerFlag != null) req["owner"] = ownerFlag;

        string raw = await SendAsync(req);
        // Pretty-print the server JSON for humans/agents reading stdout.
        try
        {
            var node = JsonNode.Parse(raw);
            bool ok = node?["ok"]?.GetValue<bool>() ?? false;
            Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return ok ? 0 : 1;
        }
        catch
        {
            Console.WriteLine(raw);
            return 0;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        desktop-agent — GUI operation agent for this PC (server resident in the interactive session)

        Usage: desktop-agent <command> [args]     (server: desktop-agent serve)

          ping | status | idle | monitors | windows
          pixel x y                                      color of one screen point (#RRGGBB)
          histogram [--rect x,y,w,h | --window w] [--top n]
                     dominant colors of a region (cheap fill/state verification)
          find-image --file <template.png> [--region x,y,w,h | --window w] [--threshold 0.85]
                     locate a UI element by its picture (for apps with shallow UIA)
          find-text <text> [--region x,y,w,h | --window w] [--lang ja-JP]
                     locate visible text via Windows OCR
          lease acquire [--ttl sec] | lease release [--force] | lease status
                     exclusive GUI lease: while held, other owners' input is refused.
                     Input also auto-holds a 20s lease per owner.
          screenshot [--window <hwnd|title>] [--rect x,y,w,h] [--out <path>]
                     [--format png|jpg] [--quality n] [--scale 0.5]
          snapshot [window] [--depth n] [--max n] [--region x,y,w,h] [--diff] [--full]
                     UIA tree pruned to interactive elements, compact fields, stable IDs.
                     --diff returns only added/changed/removed vs the previous snapshot.
          focus <hwnd|title>
          click <elementId> | click --xy x,y [--button left|right|middle] [--count n]
          click --anchor <hwnd|title> --rel dx,dy        window-relative (negative = from right/bottom edge)
          double_click <elementId|--xy x,y>
          right_click  <elementId|--xy x,y>
          drag x1 y1 x2 y2 [--button b]
          scroll x y [--delta n] [--horizontal]
          move x y [--rel]                               absolute, or relative delta with --rel
          path x,y x,y [x,y ...] [--duration ms]         smooth timed cursor path
          type <text...>                                 Unicode text input
          key <chord>                                    e.g. ctrl+s, alt+tab, win+r
          keydown <key> | keyup <key>                    key = name, vk:0x41, or scan:0x1E
          hold <key> <ms>                                precise key hold (server-side timer)
          raw <json events> | raw --file f.json          raw SendInput events;
                     mouse events accept pixel "x"/"y" as well as normalized "dx"/"dy"
          sequence <json steps> | sequence --file f.json [--dry-run] [--no-receipt]
                     timed batch executed server-side;
                     step = {"do":"hold","key":"w","ms":800,"delay":100}
                     conditions (precond/expect) accept pixel / regionDominant keys;
                     {"do":"checkpoint","pixel":[x,y,"#RRGGBB",tol]} verifies state,
                     {"do":"checkpoint","regionChanged":[x,y,w,h]} verifies change
                     vs the sequence start; a receipt crop of the touched area is
                     attached to the result. --dry-run traces geometry, presses nothing.
          setvalue <elementId> <text...>
          scrollintoview <elementId>
          launch <app> [--args "..."]
          clipboard get | clipboard set <text...>
          stop-server

        All commands print JSON. Coordinates are physical pixels (see `monitors`).
        Element IDs are stable across snapshots of the same window while the
        element's identity is unchanged.
        Global options: --hwnd <n> binds input to that window (refused when it
        is not in front; on sequence it also guards every step), --owner <name>
        identifies this client for the focus lease (env: DESKTOP_AGENT_OWNER).
        """);
    }
}
