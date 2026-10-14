using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexQuota.FakeServer;

/// <summary>
/// Synthetic JSONL helper used only by protocol tests. It is never referenced by
/// production configuration and accepts explicit scenarios so transport behavior can
/// be exercised deterministically. It prints nothing except protocol frames.
/// </summary>
internal static class Program
{
    private static string _scenario = "happy";
    private static StreamWriter _out = null!;

    private static async Task<int> Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--scenario")
            {
                _scenario = args[i + 1];
            }
            else if (args[i] == "--pidfile")
            {
                await File.WriteAllTextAsync(args[i + 1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            else if (args[i] == "--append")
            {
                await File.AppendAllTextAsync(
                    args[i + 1],
                    "start:" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
            }
        }

        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        if (_scenario == "sleep")
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (_scenario is "spawn-child" or "graceful-child")
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--childpidfile")
                {
                    var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { "--scenario", "sleep" },
                    });
                    if (child is not null)
                    {
                        await File.WriteAllTextAsync(args[i + 1], child.Id.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (true)
        {
            string? line = await input.ReadLineAsync();
            if (line is null)
            {
                if (_scenario == "spawn-child")
                {
                    // Hold the tree open so disposal must terminate it explicitly.
                    Thread.Sleep(Timeout.Infinite);
                }

                return 0;
            }

            if (line.Length == 0)
            {
                continue;
            }

            HandleLine(line);
        }
    }

    private static void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
            {
                return;
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string method = methodElement.GetString()!;

            if (_scenario == "hang-all")
            {
                return;
            }

            switch (method)
            {
                case "initialize":
                    if (_scenario == "exit-during-init")
                    {
                        Environment.Exit(0);
                    }
                    if (_scenario == "malformed-initialize")
                    {
                        Send("this is not json");
                        return;
                    }
                    Send($"{{\"id\":{id},\"result\":{{\"userAgent\":\"fake/0.0 (test)\",\"codexHome\":\"<fake>\",\"platformFamily\":\"windows\",\"platformOs\":\"windows\"}}}}");
                    if (_scenario == "notifications")
                    {
                        Send("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                        Send("{\"id\":424242,\"result\":{\"unrelated\":true}}");
                    }

                    if (_scenario == "server-request")
                    {
                        Send("{\"id\":9001,\"method\":\"thread/start\",\"params\":{\"model\":\"x\"}}");
                    }

                    break;

                case "account/read":
                    RespondAccount(id);
                    break;

                case "account/rateLimits/read":
                    RespondRateLimits(id);
                    break;

                case "account/usage/read":
                    RespondUsage(id);
                    break;

                default:
                    Error(id, -32601, "method not found");
                    break;
            }
        }
    }

    private static void RespondAccount(int id)
    {
        if (_scenario == "exit-after-init")
        {
            Environment.Exit(0);
        }
        if (_scenario == "malformed")
        {
            Send("this is not json");
            return;
        }
        if (_scenario == "oversized")
        {
            Send("{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"result\":{\"blob\":\"" + new string('x', 20000) + "\"}}");
            return;
        }
        if (_scenario == "account-other-quota-error")
        {
            Send($"{{\"id\":{id},\"result\":{{\"account\":{{\"type\":\"chatgpt\",\"id\":\"acct-other\",\"planType\":\"plus\"}}}}}}");
            return;
        }
        if (_scenario == "oversized-unterminated")
        {
            _out.Write(new string('x', 20000));
            _out.Flush();
            return;
        }
        switch (_scenario)
        {
            case "account-null":
                Send($"{{\"id\":{id},\"result\":{{\"account\":null,\"requiresOpenaiAuth\":true}}}}");
                return;
            case "account-apikey":
                Send($"{{\"id\":{id},\"result\":{{\"account\":{{\"type\":\"apiKey\",\"id\":\"key-1\"}},\"requiresOpenaiAuth\":false}}}}");
                return;
            case "hang":
                return;
            default:
                Send($"{{\"id\":{id},\"result\":{{\"account\":{{\"type\":\"chatgpt\",\"id\":\"acct-test\",\"planType\":\"plus\"}},\"requiresOpenaiAuth\":false}}}}");
                return;
        }
    }

    private static void RespondRateLimits(int id)
    {
        if (_scenario == "account-updated")
        {
            // Emitted during the normal quota cycle so mixed-account data must be rejected
            // even though the optional official daily read is no longer requested.
            Send("{\"method\":\"account/updated\",\"params\":{\"authMode\":\"chatgpt\"}}");
        }

        if (_scenario is "quota-error" or "account-other-quota-error")
        {
            Error(id, -32000, "backend unavailable");
            return;
        }

        if (_scenario == "hang")
        {
            return;
        }

        const string CodexBucket =
            "{\"limitId\":\"codex\",\"limitName\":null," +
            "\"primary\":{\"usedPercent\":54,\"windowDurationMins\":300,\"resetsAt\":1791746294}," +
            "\"secondary\":{\"usedPercent\":8,\"windowDurationMins\":10080,\"resetsAt\":1792315061}," +
            "\"credits\":{\"hasCredits\":true,\"unlimited\":false,\"balance\":\"12.5\"}," +
            "\"individualLimit\":null,\"planType\":\"plus\",\"rateLimitReachedType\":null}";

        const string OtherBucket =
            "{\"limitId\":\"gpt-4o\",\"limitName\":\"辅助\",\"primary\":{\"usedPercent\":99,\"windowDurationMins\":1440,\"resetsAt\":1791746294}}";

        string payload =
            "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) +
            ",\"result\":{\"rateLimits\":" + CodexBucket +
            ",\"rateLimitsByLimitId\":{\"codex\":" + CodexBucket + ",\"gpt-4o\":" + OtherBucket + "}" +
            ",\"rateLimitResetCredits\":{\"availableCount\":0}}}";

        Send(payload);
    }

    private static void RespondUsage(int id)
    {
        if (_scenario == "usage-hang")
        {
            return;
        }
        if (_scenario == "usage-method-not-found")
        {
            Error(id, -32601, "method not found");
            return;
        }

        if (_scenario == "hang")
        {
            return;
        }

        string today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Send($"{{\"id\":{id},\"result\":{{\"summary\":{{\"lifetimeTokens\":1000}}," +
            $"\"dailyUsageBuckets\":[{{\"startDate\":\"{today}\",\"tokens\":123456}},{{\"startDate\":\"2026-01-01\",\"tokens\":7}}]}}}}");
    }

    private static void Error(int id, int code, string message) =>
        Send($"{{\"id\":{id},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}");

    private static void Send(string payload) => _out.WriteLine(payload);
}
