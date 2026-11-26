namespace FluxCord;

public sealed class RtcRegionInfo
{
    public string Id = "";
    public string Name = "";
    public int PingMs = -1; // -1 = measuring / unknown
    public bool Optimal;
    public bool IsOfficialDiscordPing;
}

public sealed class DiscordUser
{
    public string Id = "";
    public string Username = "";
    public string DisplayName = "";
    public string Avatar = "";
    public string Status = "offline"; // online, idle, dnd, offline
    public string ActivityText = "";  // custom status / game / listening

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(Id)
        ? null
        : $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.png?size=64";
}

public sealed class DiscordFriend
{
    public string UserId = "";
    public string Username = "";
    public string DisplayName = "";
    public string Avatar = "";
    public string Status = "offline";
    public string ActivityText = "";
    public int Type; // 1 = Friend, 3 = Incoming, 4 = Outgoing

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(UserId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{UserId}/{Avatar}.png?size=64";
}

public readonly record struct ChannelOverwrite(string Id, int Type, ulong Allow, ulong Deny);

public sealed class DiscordGuild
{
    public string Id = "";
    public string Name = "";
    public string Icon = "";
    public string OwnerId = "";
    public ulong EveryonePerms = ulong.MaxValue;
    public Dictionary<string, ulong> RolePerms = new();
    public HashSet<string> MyRoleIds = [];
    public int UnreadCount;
    public int MentionCount;
    public List<DiscordChannel> Channels = [];
    public List<DiscordChannel> AllChannels = [];
    public Dictionary<string, VoiceMember> VoiceStates = new();

    public string? IconUrl => string.IsNullOrEmpty(Icon)
        ? null
        : $"https://cdn.discordapp.com/icons/{Id}/{Icon}.png?size=96";
}

public sealed class DiscordChannel
{
    public string Id = "";
    public string GuildId = "";
    public string Name = "";
    public string Topic = "";
    public string RecipientId = "";
    public string RecipientAvatar = "";
    public string ChannelIcon = "";
    public List<string> RecipientIds = [];
    public int Type; // 0=Text, 1=DM, 2=Voice, 3=GroupDM, 4=Category, 5=Announcement, 13=Stage, 15=Forum
    public int Position;
    public string ParentId = "";
    public string LastMessageId = "";
    public int MentionCount;
    public List<ChannelOverwrite> Overwrites = [];
    public bool Inaccessible;
    public bool IsVoice => Type == 2 || Type == 13;
    public bool IsCategory => Type == 4;
    public bool IsDmOrGroup => Type == 1 || Type == 3;
    public int GroupMemberCount => Type == 3 ? Math.Max(2, RecipientIds.Count + 1) : (Type == 1 ? 2 : 0);

    public string? DmAvatarUrl
    {
        get
        {
            if (Type == 3 && !string.IsNullOrEmpty(ChannelIcon) && !string.IsNullOrEmpty(Id))
                return $"https://cdn.discordapp.com/channel-icons/{Id}/{ChannelIcon}.png?size=64";
            if (!string.IsNullOrEmpty(RecipientAvatar) && !string.IsNullOrEmpty(RecipientId))
                return $"https://cdn.discordapp.com/avatars/{RecipientId}/{RecipientAvatar}.png?size=64";
            return null;
        }
    }
}

public sealed class DiscordCallState
{
    public string ChannelId = "";
    public string MessageId = "";
    public string Region = "";
    public HashSet<string> Ringing = [];
    public Dictionary<string, VoiceMember> VoiceStates = new();
}

public sealed class VoiceMember
{
    public string UserId = "";
    public string ChannelId = "";
    public string Username = "";
    public string Avatar = "";
    public bool SelfMute;
    public bool SelfDeaf;
    public bool SelfStream;
    public bool Speaking;
    public long SpeakingUntil;

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(UserId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{UserId}/{Avatar}.png?size=48";
}

public sealed class DiscordAttachment
{
    public string Url = "";
    public string Filename = "";
    public long Size;
    public bool IsImage;
}

public sealed class DiscordEmbed
{
    public string Title = "";
    public string Description = "";
    public string Url = "";
    public string Author = "";
    public string Footer = "";
    public string ImageUrl = "";
    public uint Color = 0xFF5865F2;
}

public sealed class ReactionEntry
{
    public string Display = ""; // e.g. "🔥" or ":custom:"
    public string ApiKey = "";  // e.g. "🔥" or "custom:123456789"
    public int Count;
    public bool Me;
}

public sealed class DiscordMessage
{
    private static readonly string[] RuMonthsGenitive =
    [
        "", "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря"
    ];

    public string Id = "";
    public string ChannelId = "";
    public string AuthorId = "";
    public string AuthorName = "";
    public string AuthorAvatar = "";
    public uint AuthorColor = 0xFF8AB4F8;
    public string Content = "";
    public string Timestamp = "";
    public string DateLabel = "";
    public string ReplyPreview = "";
    public int MsgType;       // 0=Default, 19=Reply, 3=Call, 1..2/4..11=System
    public bool CallActive;   // Ongoing call right now ("Присоединиться к звонку")
    public bool CallMissed;   // Missed call (grey icon vs green icon)
    public List<string> AttachmentUrls = [];
    public List<DiscordAttachment> Attachments = [];
    // Immutable / copy-on-write arrays so Gateway thread mutations NEVER throw InvalidOperationException on Direct2D UI render thread!
    public DiscordEmbed[] Embeds = [];
    public ReactionEntry[] Reactions = [];
    public bool IsEdited;
    public bool Pending;

    public bool IsSystemOrCall => MsgType != 0 && MsgType != 19 && MsgType != 20 && MsgType != 21;

    public string? AvatarUrl => string.IsNullOrEmpty(AuthorAvatar) || string.IsNullOrEmpty(AuthorId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{AuthorId}/{AuthorAvatar}.png?size=64";

    public static string FormatRussianDate(DateTime localDt)
    {
        int m = Math.Clamp(localDt.Month, 1, 12);
        return $"{localDt.Day} {RuMonthsGenitive[m]} {localDt.Year} г.";
    }

    public static string DateLabelFromSnowflake(string id)
    {
        if (ulong.TryParse(id, out var sf) && sf > 4194304UL)
        {
            long unixMs = (long)(sf >> 22) + 1420070400000L;
            try
            {
                var localDt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().DateTime;
                return FormatRussianDate(localDt);
            }
            catch { }
        }
        return "";
    }
}
