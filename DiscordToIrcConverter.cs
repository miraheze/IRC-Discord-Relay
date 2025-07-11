using Discord.WebSocket;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace IrcDiscordRelay
{
    internal static class DiscordToIrcConverter
    {
        private static readonly Regex BoldRegex = new(@"\*\*(.*?)\*\*");
        private static readonly Regex ItalicRegex = new(@"\*(.*?)\*");
        private static readonly Regex UnderlineRegex = new(@"__(.*?)__");
        private static readonly Regex StrikethroughRegex = new(@"~~(.*?)~~");
        private static readonly Regex SlashCommandRegex = new(@"<\/(\w+):?\d*>");
        private static readonly Regex EmojiRegex = new(@"<([A-Za-z0-9-_]?:[A-Za-z0-9-_]+:)[0-9]+>");

        public static string Convert(SocketMessage message)
        {
            string messageContent = message.Content;

            // Replace Discord markdown with IRC formatting codes
            messageContent = BoldRegex.Replace(messageContent, "\x02$1\x02");
            messageContent = ItalicRegex.Replace(messageContent, "\x1D$1\x1D");
            messageContent = UnderlineRegex.Replace(messageContent, "\x1F$1\x1F");
            messageContent = StrikethroughRegex.Replace(messageContent, "\x1E$1\x1E");

            // Parse <:emoji:0123456789> to :emoji:
            messageContent = EmojiRegex.Replace(messageContent, "$1");

            // Parse mentions
            foreach (SocketUser userMention in message.MentionedUsers)
            {
                messageContent = messageContent.Replace($"<@{userMention.Id}>", $"@{userMention.Username}");
            }

            foreach (SocketRole roleMention in message.MentionedRoles)
            {
                messageContent = messageContent.Replace($"<@&{roleMention.Id}>", $"@{roleMention.Name}");
            }

            foreach (SocketGuildChannel channelMention in message.MentionedChannels)
            {
                messageContent = messageContent.Replace($"<#{channelMention.Id}>", $"#{channelMention.Name}");
            }

            // Support attachments
            if (message.Attachments.Any())
            {
                IEnumerable<string> attachments = message.Attachments.Select(x => x.Url);
                messageContent += "\n" + string.Join("\n", attachments);
            }

            // Parse slash commands
            messageContent = SlashCommandRegex.Replace(messageContent, m =>
            {
                string tag = m.Value;
                string commandName = tag[2..^1].Split(':')[0];
                return $"/{commandName}";
            });

            return messageContent;
        }
    }
}
