using Discord;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.WebSocket;
using IniParser;
using IniParser.Model;
using Meebey.SmartIrc4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IrcDiscordRelay
{
    internal class IrcDiscordRelay
    {
        private const int MAX_MESSAGE_LENGTH = 400;

        private readonly string ircServer;
        private readonly int ircPort;
        private readonly string ircNickname;
        private readonly string ircRealname;
        private readonly string ircUsername;
        private readonly string ircPassword;
        private readonly bool ircUseSSL;
        private readonly bool ircUseSasl;
        private readonly ProxyType ircProxyType;
        private readonly string ircProxyHost;
        private readonly int ircProxyPort;
        private readonly string ircProxyUsername;
        private readonly string ircProxyPassword;
        private readonly string discordBotToken;
        private readonly string discordProxy;
        private readonly bool discordIncludeEdited;

        private readonly Dictionary<ulong, string> discordToIrcChannelMap;
        private readonly Dictionary<string, ulong> ircToDiscordChannelMap;
        private readonly Dictionary<string, IMessageChannel> discordChannelsMap;

        private HashSet<string> ircIgnoredUsers;
        private HashSet<Regex> ircIgnoredUsersRegex;
        private HashSet<string> discordIgnoredUsers;
        private HashSet<Regex> discordIgnoredUsersRegex;

        private readonly IrcClient ircClient;
        private readonly DiscordSocketClient discordClient;

        public IrcDiscordRelay()
        {
            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            ircServer = data["IRC"]["Server"];
            ircPort = int.Parse(data["IRC"]["Port"]);
            ircNickname = data["IRC"]["Nickname"];
            ircRealname = data["IRC"]["Realname"] ?? ircNickname;
            ircUsername = data["IRC"]["Username"] ?? ircNickname;
            ircPassword = data["IRC"]["Password"];
            ircUseSSL = bool.Parse(data["IRC"]["UseSSL"]);
            ircUseSasl = bool.Parse(data["IRC"]["UseSASL"] ?? "false");

            if (ircUseSasl && string.IsNullOrEmpty(ircPassword))
            {
                throw new InvalidOperationException("UseSASL needs a Password in the IRC section.");
            }

            string ircProxy = data["IRC"]["Proxy"];
            if (!string.IsNullOrWhiteSpace(ircProxy))
            {
                (ircProxyType, ircProxyHost, ircProxyPort, ircProxyUsername, ircProxyPassword) = ParseIrcProxy(ircProxy.Trim());
            }

            discordBotToken = data["Discord"]["BotToken"];
            discordProxy = data["Discord"]["Proxy"];
            discordIncludeEdited = bool.Parse(data["Discord"]["IncludeEdited"] ?? "false");

            discordToIrcChannelMap = new Dictionary<ulong, string>();
            ircToDiscordChannelMap = new Dictionary<string, ulong>();

            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData key in channelMappingSection.Keys)
                {
                    ulong discordChannelId = ulong.Parse(key.KeyName);
                    string ircChannel = key.Value;
                    discordToIrcChannelMap[discordChannelId] = ircChannel;
                    ircToDiscordChannelMap[ircChannel] = discordChannelId;
                }
            }

            discordChannelsMap = new Dictionary<string, IMessageChannel>();

            ParseIgnoreUsers(data);

            // Create the IRC client and register event handlers
            ircClient = new IrcClient
            {
                Encoding = Encoding.UTF8,
                EnableUTF8Recode = true,
                AutoReconnect = true,
                AutoRejoin = true,

                SendDelay = 300,

                UseSsl = ircUseSSL,

                ProxyType = ircProxyType,
                ProxyHost = ircProxyHost,
                ProxyPort = ircProxyPort,
                ProxyUsername = ircProxyUsername,
                ProxyPassword = ircProxyPassword
            };
            ircClient.OnConnected += IrcClient_OnConnected;
            ircClient.OnDisconnected += IrcClient_OnDisconnected;
            ircClient.OnChannelMessage += IrcClient_OnChannelMessage;
            ircClient.OnChannelNotice += IrcClient_OnChannelNotice;
            ircClient.OnChannelAction += IrcClient_OnChannelAction;
            ircClient.OnError += IrcClient_OnError;
            ircClient.OnReadLine += IrcClient_OnReadLine;

            if (discordProxy != null)
            {
                HttpClient.DefaultProxy = new WebProxy(discordProxy);
            }

            // Create the Discord client
            discordClient = new DiscordSocketClient(
                new DiscordSocketConfig
                {
                    FormatUsersInBidirectionalUnicode = false,
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
                    RestClientProvider = DefaultRestClientProvider.Create(useProxy: discordProxy != null),
                    WebSocketProvider = DefaultWebSocketProvider.Create(discordProxy != null ? HttpClient.DefaultProxy : null)
                }
            );

            discordClient.MessageReceived += DiscordClient_MessageReceived;

            if (discordIncludeEdited)
            {
                discordClient.MessageUpdated += DiscordClient_MessageUpdated;
            }
        }

        public async Task Start()
        {
            // Connect to the IRC server and start listening for messages
            ConnectToIrcServer();
            ircClient.Login(ircNickname, ircRealname, 4, ircUsername, ircUseSasl ? null : ircPassword);

            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            // Join all the IRC channels specified in the configuration file
            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData channelKey in channelMappingSection.Keys)
                {
                    ircClient.RfcJoin(channelKey.Value);
                }
            }

            // Connect to the Discord bot
            await discordClient.LoginAsync(TokenType.Bot, discordBotToken);
            await discordClient.StartAsync();

            // Map each Discord channel to its corresponding IMessageChannel object
            foreach (KeyValuePair<ulong, string> entry in discordToIrcChannelMap)
            {
                try
                {
                    ulong discordChannelId = entry.Key;
                    string ircChannel = entry.Value;

                    IMessageChannel message = await discordClient.Rest.GetChannelAsync(discordChannelId) as IMessageChannel;
                    discordChannelsMap[ircChannel] = message;
                }
                catch (Discord.Net.HttpException e) when (e.DiscordCode == DiscordErrorCode.MissingPermissions)
                {
                    // Ignore, bot does not have permissions to view the channel
                }
            }

            ircClient.Listen();
        }

        public async Task Stop()
        {
            // Disconnect from the IRC server and stop listening for messages
            ircClient.Disconnect();
            ircClient.OnChannelMessage -= IrcClient_OnChannelMessage;
            ircClient.OnChannelNotice -= IrcClient_OnChannelNotice;
            ircClient.OnChannelAction -= IrcClient_OnChannelAction;
            ircClient.OnError -= IrcClient_OnError;
            ircClient.OnReadLine -= IrcClient_OnReadLine;

            // Disconnect from the Discord bot
            await discordClient.StopAsync();
            await discordClient.LogoutAsync();
        }

        private void ConnectToIrcServer()
        {
            if (ircProxyType == ProxyType.None)
            {
                IPAddress host = Dns.GetHostAddresses(ircServer)
                    .First(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                ircClient.Connect(host.ToString(), ircPort);
            }
            else if (ircProxyType == ProxyType.Socks4)
            {
                // SOCKS4 only takes an IPv4 address. Resolve it here.
                IPAddress host = Dns.GetHostAddresses(ircServer)
                    .First(a => a.AddressFamily == AddressFamily.InterNetwork);
                ircClient.Connect(host.ToString(), ircPort);
            }
            else
            {
                // The proxy resolves the hostname. Local DNS stays out of it.
                ircClient.Connect(ircServer, ircPort);
            }
        }

        private static (ProxyType Type, string Host, int Port, string Username, string Password) ParseIrcProxy(string proxy)
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri uri) || string.IsNullOrEmpty(uri.Host))
            {
                throw new FormatException("Invalid IRC proxy. Expected a URL like socks5://127.0.0.1:1080.");
            }

            ProxyType type = uri.Scheme.ToLowerInvariant() switch
            {
                "http" => ProxyType.Http,
                "socks4" => ProxyType.Socks4,
                "socks4a" => ProxyType.Socks4a,
                "socks5" => ProxyType.Socks5,
                _ => throw new FormatException($"Unsupported IRC proxy scheme '{uri.Scheme}'. Use http, socks4, socks4a or socks5.")
            };

            if (uri.Port <= 0)
            {
                throw new FormatException("The IRC proxy needs a port.");
            }

            string username = null;
            string password = null;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                string[] credentials = uri.UserInfo.Split(':', 2);
                username = Uri.UnescapeDataString(credentials[0]);
                password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty;
            }

            return (type, uri.Host, uri.Port, username, password);
        }

        private void ParseIgnoreUsers(IniData data)
        {
            SectionData ignoreUsersSection = data.Sections.GetSectionData("IgnoreUsers");
            if (ignoreUsersSection != null)
            {
                KeyData ircIgnoreUsers = ignoreUsersSection.Keys.FirstOrDefault(k => k.KeyName == "IRC");
                if (ircIgnoreUsers != null)
                {
                    ircIgnoredUsers = new HashSet<string>(ircIgnoreUsers.Value.Split(',').Select(val => val.Trim()));
                    ircIgnoredUsers = new HashSet<string>(ircIgnoredUsers.Where(val => !val.StartsWith("/") || !val.EndsWith("/") ||
                        !Regex.IsMatch("", val.Trim('/'), RegexOptions.IgnoreCase)));
                    ircIgnoredUsersRegex = new HashSet<Regex>(ircIgnoredUsers.Where(val => val.StartsWith("/") && val.EndsWith("/"))
                        .Select(val => new Regex(val.Trim('/'), RegexOptions.IgnoreCase)));
                }

                KeyData discordIgnoreUsers = ignoreUsersSection.Keys.FirstOrDefault(k => k.KeyName == "Discord");
                if (discordIgnoreUsers != null)
                {
                    discordIgnoredUsers = new HashSet<string>(discordIgnoreUsers.Value.Split(',').Select(val => val.Trim()));
                    discordIgnoredUsers = new HashSet<string>(discordIgnoredUsers.Where(val => !val.StartsWith("/") || !val.EndsWith("/") ||
                        !Regex.IsMatch("", val.Trim('/'), RegexOptions.IgnoreCase)));
                    discordIgnoredUsersRegex = new HashSet<Regex>(discordIgnoredUsers.Where(val => val.StartsWith("/") && val.EndsWith("/"))
                        .Select(val => new Regex(val.Trim('/'), RegexOptions.IgnoreCase)));
                }
            }
        }

        private async Task DiscordClient_MessageReceived(SocketMessage message)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                message.Author.Id == discordClient.CurrentUser.Id ||
                discordIgnoredUsers.Contains(message.Author.Username) ||
                discordIgnoredUsersRegex.Any(r => r.IsMatch(message.Author.Username))
            )
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                // Determine if this message is a reply to another message
                string author = $"<{message.Author}>";
                if (message.Reference != null)
                {
                    IMessage repliedToMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);

                    // If replying to the bot, return the actual user that the reply is for
                    if (repliedToMessage.Author.Id == discordClient.CurrentUser.Id)
                    {
                        string[] parts = repliedToMessage.Content.Split(' ');
                        if (parts.Length > 1 && parts[0].StartsWith("<") && parts[0].EndsWith(">"))
                        {
                            author = $"<{message.Author}, replying to {parts[0][1..^1]}>";
                        }
                    }
                    else
                    {
                        author = $"<{message.Author}, replying to {repliedToMessage.Author}>";
                    }
                }

                await SendMessageToIrcChannel(ircChannel, DiscordToIrcConverter.Convert(message), author);
            }
        }

        private async Task DiscordClient_MessageUpdated(Cacheable<IMessage, ulong> cacheable, SocketMessage message, ISocketMessageChannel channel)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                message.Author.Id == discordClient.CurrentUser.Id ||
                discordIgnoredUsers.Contains(message.Author.Username) ||
                discordIgnoredUsersRegex.Any(r => r.IsMatch(message.Author.Username))
            )
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                // Determine if this message is a reply to another message
                string author = $"<{message.Author}>";
                if (message.Reference != null)
                {
                    IMessage repliedToMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);

                    // If replying to the bot, return the actual user that the reply is for
                    if (repliedToMessage.Author.Id == discordClient.CurrentUser.Id)
                    {
                        string[] parts = repliedToMessage.Content.Split(' ');
                        if (parts.Length > 1 && parts[0].StartsWith("<") && parts[0].EndsWith(">"))
                        {
                            author = $"<{message.Author}, replying to {parts[0][1..^1]}>";
                        }
                    }
                    else
                    {
                        author = $"<{message.Author}, replying to {repliedToMessage.Author}>";
                    }
                }

                author += " (edited)";

                await SendMessageToIrcChannel(ircChannel, DiscordToIrcConverter.Convert(message), author);
            }
        }

        private async Task SendMessageToIrcChannel(string ircChannel, string message, string author)
        {
            int maxMessageLength = MAX_MESSAGE_LENGTH - author.Length - 5;
            string[] chunks = SplitMessage(message, maxMessageLength);

            for (int i = 0; i < chunks.Length; i++)
            {
                string formattedLine = author;
                int messageNumber = i + 1;
                int totalMessages = chunks.Length;

                if (chunks.Length > 1)
                {
                    formattedLine += $" [{messageNumber}/{totalMessages}]";
                }

                formattedLine += $" {chunks[i]}";

                await Task.Run(() => ircClient.SendMessage(SendType.Message, ircChannel, formattedLine));
            }
        }

        private static string[] SplitMessage(string message, int chunkSize)
        {
            if (message == null || chunkSize <= 0)
            {
                return new[] { message };
            }

            List<string> parts = new();

            string[] lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (line.Length == 0 || line.Trim().Length == 0)
                {
                    continue;
                }

                if (line.Length <= chunkSize)
                {
                    parts.Add(line);
                }
                else
                {
                    int startIndex = 0;
                    while (startIndex < line.Length)
                    {
                        int length = Math.Min(chunkSize, line.Length - startIndex);
                        string part = line.Substring(startIndex, length);
                        parts.Add(part);
                        startIndex += length;
                    }
                }
            }

            return parts.ToArray();
        }

        private void IrcClient_OnConnected(object sender, EventArgs e)
        {
            // Ask for SASL before NICK and USER go out. Registration waits for CAP END.
            if (ircUseSasl)
            {
                ircClient.WriteLine("CAP REQ :sasl", Priority.Critical);
            }

            // Notify the user that the IRC client has successfully connected
            Console.WriteLine($"Connected to {ircServer}:{ircPort}.");
        }

        private void IrcClient_OnReadLine(object sender, ReadLineEventArgs e)
        {
            if (!ircUseSasl)
            {
                return;
            }

            // Drop any message tags and the prefix, then split into command and parameters
            string line = e.Line;
            if (line.StartsWith('@'))
            {
                int tagEnd = line.IndexOf(' ');
                if (tagEnd < 0)
                {
                    return;
                }

                line = line[(tagEnd + 1)..];
            }

            if (line.StartsWith(':'))
            {
                int prefixEnd = line.IndexOf(' ');
                if (prefixEnd < 0)
                {
                    return;
                }

                line = line[(prefixEnd + 1)..];
            }

            string[] parts = line.Split(' ', 4);

            try
            {
                if (parts[0] == "CAP" && parts.Length == 4 && parts[2] == "ACK" && parts[3].Contains("sasl"))
                {
                    ircClient.WriteLine("AUTHENTICATE PLAIN", Priority.Critical);
                }
                else if (parts[0] == "CAP" && parts.Length >= 3 && parts[2] == "NAK")
                {
                    Console.WriteLine("IRC server refused SASL. Continuing without it.");
                    ircClient.WriteLine("CAP END", Priority.Critical);
                }
                else if (parts[0] == "AUTHENTICATE" && parts.Length >= 2 && parts[1] == "+")
                {
                    SendSaslCredentials();
                }
                else if (parts[0] == "903")
                {
                    Console.WriteLine($"Authenticated to {ircServer} with SASL.");
                    ircClient.WriteLine("CAP END", Priority.Critical);
                }
                else if (parts[0] is "904" or "905" or "906" or "907")
                {
                    // 907 means already authenticated. The rest are failures.
                    if (parts[0] != "907")
                    {
                        Console.WriteLine($"SASL authentication failed ({parts[0]}). Continuing without it.");
                    }

                    ircClient.WriteLine("CAP END", Priority.Critical);
                }
            }
            catch (NotConnectedException)
            {
                // The connection dropped mid handshake. The reconnect logic starts it over.
            }
        }

        private void SendSaslCredentials()
        {
            // PLAIN is an empty authzid, the account name and the password, split by NUL bytes
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ircNickname}\0{ircPassword}"));

            // Long payloads go out in 400 byte pieces, with a lone + if the last piece is full
            for (int i = 0; i < payload.Length; i += 400)
            {
                ircClient.WriteLine($"AUTHENTICATE {payload.Substring(i, Math.Min(400, payload.Length - i))}", Priority.Critical);
            }

            if (payload.Length % 400 == 0)
            {
                ircClient.WriteLine("AUTHENTICATE +", Priority.Critical);
            }
        }

        private void IrcClient_OnDisconnected(object sender, EventArgs e)
        {
            // Reconnect to the IRC server
            ConnectToIrcServer();
            ircClient.Login(ircNickname, ircRealname, 4, ircUsername, ircUseSasl ? null : ircPassword);

            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            // Join all the IRC channels specified in the configuration file
            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData channelKey in channelMappingSection.Keys)
                {
                    ircClient.RfcJoin(channelKey.Value);
                }
            }
        }

        private void IrcClient_OnChannelMessage(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.Data.Message))}");
            }
        }

        private void IrcClient_OnChannelNotice(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC notice message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> NOTICE: {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.Data.Message.Replace("*", "\\*")))}");
            }
        }

        private void IrcClient_OnChannelAction(object sender, ActionEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Format the action message as an italicized text with the sender's nickname
                string formattedMessage = $"_**{e.Data.Nick}** {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.ActionMessage))}_";

                // Relay the action message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), formattedMessage);
            }
        }

        private void IrcClient_OnError(object sender, ErrorEventArgs e)
        {
            // Log any IRC error messages to the console
            Console.WriteLine($"IRC Error: {e.Data.Message}");
        }

        private async Task SendMessageToDiscordChannel(string discordChannelId, string message)
        {
            // Get the Discord channel by ID
            if (!discordChannelsMap.TryGetValue(discordChannelId, out IMessageChannel messageChannel))
            {
                Discord.Rest.RestChannel channel = await discordClient.Rest.GetChannelAsync(ulong.Parse(discordChannelId));
                messageChannel = channel as IMessageChannel;
                discordChannelsMap[discordChannelId] = messageChannel;
            }

            // Parse mentions
            message = await ParseIrcMentions(message, messageChannel);

            // Send the message to the Discord channel asynchronously
            _ = await messageChannel.SendMessageAsync(message);
        }

        private static async Task<string> ParseIrcMentions(string message, IMessageChannel channel)
        {
            _ = await Task.Run(() =>
            {
                List<string> mentions = message
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s.StartsWith('@') || s.EndsWith(':'))
                    .ToList();

                if (!mentions.Any())
                {
                    return message;
                }

                // Get the current Discord server
                IGuild guild = (channel as IGuildChannel)?.Guild;

                // Parse mentions to Discord mentions
                foreach (string mention in mentions)
                {
                    // Convert to lower case for case-insensitive comparison
                    string mentionLower = mention.ToLowerInvariant();

                    // Replace @everyone and @here with escaped versions
                    if (mentionLower.StartsWith("@everyone") || mentionLower.StartsWith("@here"))
                    {
                        message = message.Replace(mention, $"`{mention}`");
                        continue;
                    }

                    // Remove the @, :, or , characters from the mention
                    string cleanedMention = mentionLower.Trim('@', ':', ',');

                    // Try to find the user by username
                    IReadOnlyCollection<IGuildUser> users = guild?.SearchUsersAsync(cleanedMention)?.Result;

                    // Check if there is at least one matching user
                    if (users != null && users.Count >= 1)
                    {
                        IGuildUser user = users.FirstOrDefault(u => u.Username.ToLowerInvariant() == cleanedMention);

                        // Replace the mention with a Discord mention if found, or leave it as is otherwise
                        if (user != null)
                        {
                            message = message.Replace(mention, $"<@{user.Id}>");
                        }
                    }
                }

                return message;
            });

            return message;
        }
    }
}
