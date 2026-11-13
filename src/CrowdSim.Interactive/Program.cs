using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CrowdNav;

namespace CrowdSim.Interactive;

public static class Program
{
    private const string Url = "http://127.0.0.1:8799";

    public static async Task Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string contentRoot = File.Exists(Path.Combine(baseDir, "wwwroot", "index.html"))
            ? baseDir
            : Directory.GetCurrentDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, max-age=0";
            }
        });

        app.MapGet("/health", () => Results.Text("ok"));
        app.Map("/ws", HandleSocket);

        Console.WriteLine($"Crowd interactive demo: {Url}");
        app.Run(Url);
        await Task.CompletedTask;
    }

    private static async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new DemoSession();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var sendLock = new SemaphoreSlim(1, 1);

        async Task Send(string json)
        {
            if (socket.State != WebSocketState.Open) return;
            await sendLock.WaitAsync(cts.Token);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                sendLock.Release();
            }
        }

        try
        {
            await Send(session.BuildWorldJson());
            await Send(session.BuildStateJson("init"));

            Task timerTask = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 60.0));
                int tick = 0;
                try
                {
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        session.Step(session.Speed);
                        if (++tick % 2 == 0)
                            await Send(session.BuildStateJson("state"));
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            });

            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        cts.Cancel();
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (cts.IsCancellationRequested) break;
                string text = Encoding.UTF8.GetString(message.ToArray());
                if (string.IsNullOrWhiteSpace(text)) continue;
                await HandleCommand(session, text, Send);
            }

            cts.Cancel();
            try { await Task.WhenAny(timerTask, Task.Delay(1000)); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Console.WriteLine("socket error: " + ex.Message);
        }
    }

    private static async Task HandleCommand(DemoSession session, string text,
        Func<string, Task> send)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "map":
                {
                    string id = root.TryGetProperty("id", out JsonElement mid) ? mid.GetString() ?? "" : "";
                    int count = root.TryGetProperty("count", out JsonElement mc) ? mc.GetInt32() : 120;
                    bool ok = session.SetMap(id, count, out string error);
                    if (ok)
                    {
                        Console.WriteLine($"map {id} count={count}");
                        await send(session.BuildWorldJson());
                        await send(session.BuildStateJson("reset"));
                    }
                    else
                    {
                        await send("{\"type\":\"map\",\"ok\":false,\"id\":\"" + Escape(id) +
                                   "\",\"error\":\"" + Escape(error) + "\"}");
                        await send("{\"type\":\"toast\",\"kind\":\"error\",\"text\":\"" +
                                   Escape(error) + "\"}");
                    }
                    break;
                }
                case "units":
                {
                    int count = root.TryGetProperty("count", out JsonElement uc) ? uc.GetInt32() : 120;
                    float size = root.TryGetProperty("size", out JsonElement us) ? us.GetSingle() : 1f;
                    float speed = root.TryGetProperty("speed", out JsonElement uv) ? uv.GetSingle() : 3.1f;
                    bool ok = session.SetUnitConfig(0.35f * size, speed, count, out string error);
                    if (ok)
                    {
                        Console.WriteLine($"units size={size:0.00} speed={speed:0.0}");
                        await send(session.BuildWorldJson());
                        await send(session.BuildStateJson("reset"));
                    }
                    else
                    {
                        await send("{\"type\":\"units\",\"ok\":false,\"error\":\"" +
                                   Escape(error) + "\"}");
                        await send("{\"type\":\"toast\",\"kind\":\"error\",\"text\":\"" +
                                   Escape(error) + "\"}");
                    }
                    break;
                }
                case "reset":
                {
                    int count = root.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 120;
                    bool ok = session.Reset(count, out string error);
                    if (ok)
                    {
                        Console.WriteLine($"reset count={count}");
                        await send(session.BuildStateJson("reset"));
                    }
                    else
                    {
                        await send("{\"type\":\"toast\",\"kind\":\"error\",\"text\":\"" +
                                   Escape(error) + "\"}");
                    }
                    break;
                }
                case "goal":
                {
                    float x = root.GetProperty("x").GetSingle();
                    float z = root.GetProperty("z").GetSingle();
                    var goal = new Vec2(x, z);
                    bool ok = session.SetGoal(goal, out string error);
                    if (ok)
                    {
                        Console.WriteLine($"goal {x:0.00},{z:0.00}");
                        await send(session.BuildGoalJson(true, session.CurrentGoal, ""));
                    }
                    else
                    {
                        await send(session.BuildGoalJson(false, goal, error));
                    }
                    break;
                }
                case "speed":
                {
                    int value = root.TryGetProperty("value", out JsonElement v) ? v.GetInt32() : 1;
                    session.Speed = Math.Clamp(value, 1, 4);
                    await send("{\"type\":\"speed\",\"value\":" + session.Speed + "}");
                    break;
                }
                case "ping":
                    await send("{\"type\":\"pong\"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            await send("{\"type\":\"toast\",\"kind\":\"error\",\"text\":\"" +
                       Escape(ex.Message) + "\"}");
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
