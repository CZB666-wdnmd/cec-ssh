// TargetFramework: .NET 9
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Renci.SshNet;
using Renci.SshNet.Common;

class SshConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

class Program
{
    static async Task<int> Main(string[] rawArgs)
    {
        // 先解析 --config / -c （如果存在），其余参数视为要传给 cec-ctl 的参数
        var args = new List<string>(rawArgs ?? Array.Empty<string>());
        string explicitConfigPath = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--config" || args[i] == "-c")
            {
                if (i + 1 < args.Count)
                {
                    explicitConfigPath = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                    break;
                }
                else
                {
                    Console.Error.WriteLine("错误：--config 需要一个路径参数。");
                    return 2;
                }
            }
        }

        // 剩下的 args 是给 cec-ctl 的参数
        if (args.Count == 0)
        {
            Console.WriteLine("用法: cec-ssh.exe [--config <path>] <cec-ctl 参数...>");
            Console.WriteLine("示例: cec-ssh.exe -d /dev/cec1 -M");
            return 1;
        }

        string configPath;
        if (!string.IsNullOrEmpty(explicitConfigPath))
        {
            configPath = explicitConfigPath;
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"指定的配置文件不存在: {configPath}");
                return 3;
            }
        }
        else
        {
            // 未显式指定：按规则在当前目录和 PATH 中查找 config.json
            configPath = FindConfigInCurrentAndPath("cec-ssh_config.json", out var triedPaths);
            if (configPath == null)
            {
                Console.Error.WriteLine("未找到配置文件 cec-ssh_config.json。已尝试以下路径：");
                foreach (var p in triedPaths) Console.Error.WriteLine("  " + p);
                Console.Error.WriteLine("你可以通过 --config <path> 指定配置文件路径。");
                return 4;
            }
        }

        SshConfig cfg;
        try
        {
            var cfgText = await File.ReadAllTextAsync(configPath);
            cfg = JsonSerializer.Deserialize<SshConfig>(cfgText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (cfg == null) throw new Exception("解析 config.json 失败");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("读取/解析 config.json 失败: " + ex.Message);
            return 5;
        }

        // 把剩余参数转为远端命令
        string command = "cec-ctl " + string.Join(" ", args.Select(QuoteForShell));
        string remoteCmd = "exec " + command;

        var connectionInfo = new ConnectionInfo(cfg.Host, cfg.Port,
            cfg.Username,
            new PasswordAuthenticationMethod(cfg.Username, cfg.Password)
        );

        using var client = new SshClient(connectionInfo);
        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SSH 连接失败: " + ex.Message);
            return 6;
        }

        var shellStream = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // 不让本程序立即退出，先转发 Ctrl+C
            try
            {
                if (client.IsConnected && shellStream.CanWrite)
                {
                    shellStream.Write("\x03");
                    shellStream.Flush();
                }
            }
            catch
            {
                // ignore
            }
        };

        // --- 吞掉远端欢迎信息（MOTD / prompt） ---
        await Task.Delay(300);
        try
        {
            var drainBuf = new byte[2048];
            // 如果有大量欢迎信息/延迟输出，短暂停顿以收集更多输出
            int emptyLoops = 0;
            while (shellStream.DataAvailable || emptyLoops < 2)
            {
                if (shellStream.DataAvailable)
                {
                    int r = shellStream.Read(drainBuf, 0, drainBuf.Length);
                    if (r <= 0) break;
                }
                else
                {
                    emptyLoops++;
                    await Task.Delay(50);
                }
            }
        }
        catch
        {
            // 忽略读取错误，继续
        }

        // 开始流式读取并打印远端输出
        var readerTask = Task.Run(async () =>
        {
            var reader = new StreamReader(shellStream, Encoding.UTF8);
            var buffer = new char[1024];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    Console.Write(new string(buffer, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }, ct);

        // 发送 stty -echo 然后 exec 命令（防止回显）
        try
        {
            string finalRemote = $"stty -echo; {remoteCmd}";
            shellStream.WriteLine(finalRemote);
            shellStream.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("向远端写入命令失败: " + ex.Message);
            cts.Cancel();
        }

        var monitorTask = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!client.IsConnected) break;
                Thread.Sleep(200);
            }
        }, ct);

        await Task.WhenAny(readerTask, monitorTask);

        cts.Cancel();

        try
        {
            await Task.WhenAny(readerTask, Task.Delay(2000));
        }
        catch { }

        try { if (client.IsConnected) client.Disconnect(); }
        catch { /* ignore */ }

        return 0;
    }

    static string FindConfigInCurrentAndPath(string fileName, out List<string> triedPaths)
    {
        triedPaths = new List<string>();

        // 1) 当前工作目录
        var cur = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        triedPaths.Add(cur);
        if (File.Exists(cur)) return cur;

        // 2) PATH 中的每个目录
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in parts)
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                triedPaths.Add(candidate);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略路径拼接或权限异常
            }
        }

        return null;
    }

    // 把单个参数转成对 shell 安全的字符串（尽量简单且兼容 sh）
    static string QuoteForShell(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";

        if (!s.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '\\' || c == '$' || c == '`' || c == '*' || c == '?' || c == '[' || c == ']' || c == ')' || c == '(' || c == '>' || c == '<' || c == '|' || c == '&' || c == ';'))
            return s;

        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }
}
