using System.Text;
using System.Text.Json;
using QRCoder;
using Weixin.Channel;

Console.OutputEncoding = Encoding.UTF8;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var arguments = args.ToList();
var stateDirectory = TakeOption(arguments, "--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weixin.Channel");
var echo = arguments.Remove("--echo");
if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

try
{
    await using var channel = new WeixinChannel(new WeixinOptions
    {
        StateDirectory = stateDirectory,
        BotAgent = "Weixin.Cli/1.0.0",
        Log = message => Console.Error.WriteLine(message)
    });
    return await ExecuteAsync(arguments, channel, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.Error.WriteLine("操作已取消。");
    return 0;
}
catch (IOException error) when (error.Message.StartsWith("状态目录正被另一个进程使用", StringComparison.Ordinal))
{
    Console.Error.WriteLine("状态目录正被另一个进程使用。请停止占用该目录的 CLI/Web，或选择另一独立目录；监听时请使用同进程 send 命令。");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"操作失败（{error.GetType().Name}）。请检查参数、状态目录是否被另一进程占用，以及渠道状态日志。");
    return 1;
}

async Task<int> ExecuteAsync(List<string> command, WeixinChannel channel, CancellationToken ct)
{
    switch (command[0])
    {
        case "login":
            var login = await channel.LoginAsync(new LoginCallbacks
            {
                Updated = (update, _) =>
                {
                    Console.WriteLine($"登录状态：{update.Status}（二维码 {update.RefreshCount}）");
                    if (update.QrContent is not null)
                    {
                        using var data = QRCodeGenerator.GenerateQrCode(update.QrContent, QRCodeGenerator.ECCLevel.M);
                        using var qr = new AsciiQRCode(data);
                        Console.WriteLine(qr.GetGraphic(1));
                        Console.WriteLine(update.QrContent);
                    }
                    return ValueTask.CompletedTask;
                },
                RequestVerificationCode = async (retry, token) =>
                {
                    Console.Write(retry ? "验证码不匹配，请重新输入：" : "请输入手机微信显示的验证码：");
                    return (await Console.In.ReadLineAsync(token) ?? throw new OperationCanceledException(token)).Trim();
                }
            }, ct);
            Print(login);
            break;
        case "accounts":
        case "status":
            Print(channel.GetAccounts());
            break;
        case "allowlist":
            Require(command, 2, "allowlist <account> [peer ...]");
            channel.ConfigureAccount(command[1], new AccountConfiguration(Allowlist: command.Skip(2).ToArray()));
            Print(channel.GetAccounts());
            break;
        case "enable":
        case "disable":
            Require(command, 2, "enable|disable <account>");
            channel.ConfigureAccount(command[1], new AccountConfiguration(Enabled: command[0] == "enable"));
            break;
        case "delete":
            Require(command, 2, "delete <account>");
            await channel.DeleteAccountAsync(command[1], ct);
            break;
        case "listen":
            await ListenAsync(command, channel, ct);
            break;
        case "send":
            Require(command, 2, "send <peer> [text] [--media path-or-url] [--account id] [--delivery id]");
            var account = TakeOption(command, "--account");
            var media = TakeOption(command, "--media");
            var delivery = TakeOption(command, "--delivery");
            var sent = await channel.SendAsync(new SendRequest { PeerId = command[1], Text = string.Join(' ', command.Skip(2)), Media = media, AccountId = account, DeliveryId = delivery }, ct);
            Print(sent);
            return sent.Status is "success" or "cancelled" ? 0 : 1;
        case "reply":
            Require(command, 2, "reply <delivery> [text] [--media path-or-url]");
            var replyMedia = TakeOption(command, "--media");
            var replied = await channel.ReplyAsync(command[1], string.Join(' ', command.Skip(2)), replyMedia, ct);
            Print(replied);
            return replied.Status is "success" or "cancelled" ? 0 : 1;
        case "typing":
            var typingAccount = TakeOption(command, "--account");
            Require(command, 2, "typing <peer> [seconds] [--account id]");
            var seconds = command.Count > 2 ? int.Parse(command[2], System.Globalization.CultureInfo.InvariantCulture) : 10;
            if (seconds is < 1 or > 3600) throw new ArgumentException("seconds 必须在 1 至 3600 之间。");
            await channel.WithTypingAsync(command[1], token => Task.Delay(TimeSpan.FromSeconds(seconds), token), typingAccount, cancellationToken: ct);
            break;
        case "progress":
            var progressAccount = TakeOption(command, "--account");
            Require(command, 2, "progress <peer> [tool-name] [--account id]");
            var run = Guid.NewGuid().ToString("N");
            var call = Guid.NewGuid().ToString("N");
            var name = command.Count > 2 ? command[2] : "demo";
            try
            {
                await channel.ProgressAsync(new ToolProgress(command[1], run, call, name, AccountId: progressAccount), ct);
                await Task.Delay(1000, ct);
                await channel.ProgressAsync(new ToolProgress(command[1], run, call, name, "completed", progressAccount), ct);
            }
            finally { await channel.DrainProgressAsync(run, CancellationToken.None); }
            break;
        case "inbox":
            Print(channel.GetInbox(command.Count > 1 ? command[1] : null));
            break;
        case "receipts":
            Require(command, 2, "receipts <delivery>");
            Print(channel.GetSendSegments(command[1]));
            break;
        case "retry":
            Require(command, 2, "retry <delivery> [--lease id] [--allow-duplicate]");
            var lease = TakeOption(command, "--lease");
            var allowDuplicate = command.Remove("--allow-duplicate");
            if (allowDuplicate) Console.Error.WriteLine("已明确允许重试 unknown 发送：消息可能重复送达，请先人工核查微信聊天记录。");
            await channel.RetryAsync(command[1], lease, allowDuplicate, ct);
            break;
        default:
            PrintHelp();
            return 2;
    }
    return 0;
}

async Task ListenAsync(List<string> command, WeixinChannel channel, CancellationToken ct)
{
    var ids = command.Skip(1).ToArray();
    if (ids.Length == 0) ids = channel.GetAccounts().Where(a => a.Enabled).Select(a => a.AccountId).ToArray();
    if (ids.Length == 0) throw new InvalidOperationException("没有启用的账号，请先 login。");
    using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var tasks = ids.Select(id => channel.RunAsync(id, async (message, token) =>
    {
        Print(message);
        if (echo)
        {
            var result = await message.ReplyAsync(message.Text, cancellationToken: token);
            if (result.Status != "success") throw new InvalidOperationException("echo 回复未成功；保留投递等待重试或人工核查。");
        }
    }, listening.Token)).ToArray();
    Console.WriteLine($"正在监听 {ids.Length} 个账号，echo={(echo ? "开启" : "关闭")}。同进程可输入 send/reply/typing/progress/inbox/status；quit 退出。");
    var inputTask = ReadCommandsAsync();
    try
    {
        var completed = await Task.WhenAny(tasks.Append(inputTask));
        await completed;
    }
    finally
    {
        listening.Cancel();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (listening.IsCancellationRequested) { }
    }

    async Task ReadCommandsAsync()
    {
        while (!listening.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(listening.Token);
            if (line is null || line.Trim() == "quit") return;
            try
            {
                var words = SplitCommand(line);
                if (words.Count == 0) continue;
                if (words[0] is not ("send" or "reply" or "typing" or "progress" or "inbox" or "receipts" or "status" or "retry"))
                {
                    Console.WriteLine("监听期间支持 send/reply/typing/progress/inbox/receipts/status/retry/quit。");
                    continue;
                }
                await ExecuteAsync(words, channel, listening.Token);
            }
            catch (OperationCanceledException) when (listening.IsCancellationRequested) { return; }
            catch (Exception error) { Console.Error.WriteLine($"命令失败（{error.GetType().Name}），监听继续。"); }
        }
    }
}

static List<string> SplitCommand(string line)
{
    var words = new List<string>();
    var word = new StringBuilder();
    var quoted = false;
    foreach (var character in line)
    {
        if (character == '"') { quoted = !quoted; continue; }
        if (char.IsWhiteSpace(character) && !quoted)
        {
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        else word.Append(character);
    }
    if (quoted) throw new ArgumentException("双引号未闭合。");
    if (word.Length > 0) words.Add(word.ToString());
    return words;
}

static string? TakeOption(List<string> values, string name)
{
    var index = values.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException($"{name} 缺少值。");
    var value = values[index + 1];
    values.RemoveRange(index, 2);
    return value;
}

static void Require(List<string> values, int count, string usage)
{
    if (values.Count < count) throw new ArgumentException($"用法：{usage}");
}

static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static void PrintHelp() => Console.WriteLine("""
微信独立渠道 CLI（所有命令可加 --state <独立状态目录>）
  login                              扫码及验证码登录
  accounts | status                  查看账号/状态
  allowlist <account> [peer ...]      配置授权列表；空列表恢复仅 owner
  enable|disable|delete <account>    配置/删除账号
  listen [account ...] [--echo]      监听；echo 只有显式开启才自动回复
  send <peer> [text] [--media path-or-url] [--account id] [--delivery id]
  reply <delivery> [text] [--media path-or-url]
  typing <peer> [seconds] [--account id]
  progress <peer> [tool-name] [--account id]
  inbox [account]                    查看持久化待办
  receipts <delivery>                查询脱敏持久发送分段收据
  retry <delivery> [--lease id] [--allow-duplicate]
listen 内可直接输入发送命令，文本或含空格的路径用双引号包围。
同一状态目录只允许一个进程；需要同时收发时请使用 listen 交互命令。
""");
